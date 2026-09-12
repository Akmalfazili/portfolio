using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services.Calendar;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// See <see cref="IPriceRefreshService"/>. Assets are grouped by <see cref="Asset.QuoteProviderKind"/>
/// — not <see cref="AssetClass"/> — so each provider gets exactly one batched call per cycle, per
/// the Phase 4 decision that stocks now span two providers (Twelve Data for NYSE/NASDAQ, Yahoo for
/// SGX). A provider that throws is caught, logged, and degrades that group to its last stored
/// quote; every other group still runs — one bad provider can never take down the whole cycle.
/// </summary>
public sealed class PriceRefreshService(
    IPortfolioDbContext db,
    IQuoteProviderRouter router,
    IMarketCalendar calendar,
    IPriceUpdateBroadcaster broadcaster,
    PriceRefreshStatusStore statusStore,
    PriceRefreshStatusEnricher statusEnricher,
    ITwelveDataCreditThrottle creditThrottle,
    IServiceScopeFactory scopeFactory,
    ManualRefreshInFlightGate manualRefreshGate,
    TimeProvider timeProvider,
    IOptions<PriceRefreshOptions> options,
    ILogger<PriceRefreshService> logger) : IPriceRefreshService
{
    public Task<PriceRefreshCycleResult> RefreshDueAsync(CancellationToken cancellationToken) =>
        RunCycleAsync(force: false, cancellationToken);

    public async Task<PriceRefreshCycleResult> RefreshNowAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var lastManualStartedAt = await db.RefreshRuns
            .Where(r => r.Trigger == RefreshTrigger.Manual)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => (DateTimeOffset?)r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastManualStartedAt is { } last)
        {
            var elapsed = now - last;
            var cooldown = options.Value.ManualCooldown;
            if (elapsed < cooldown)
            {
                var remaining = (int)Math.Ceiling((cooldown - elapsed).TotalSeconds);
                return new PriceRefreshCycleResult(PriceRefreshOutcome.CooldownActive, remaining, [], 0);
            }
        }

        // D38: Twelve Data's credit-aware throttle can legitimately pace a large symbol batch
        // across several minutes (see the remarks on ITwelveDataCreditThrottle). This endpoint
        // must never block that long, so a manual trigger that would hit that path is detached
        // onto its own scope instead, and this call returns Queued promptly — "returning promptly
        // having queued the work" rather than either blocking for minutes or silently doing
        // nothing. Every other case (crypto/Yahoo only, or a Twelve Data batch that fits in one
        // throttle chunk) is unaffected and still runs synchronously exactly as before.
        if (await NeedsDetachedTwelveDataSweepAsync(now, cancellationToken))
        {
            if (!manualRefreshGate.TryEnter())
            {
                // A previous manual click's detached sweep is still running and has not yet
                // written the RefreshRun row the cooldown check above reads from — reuse the same
                // "try again shortly" outcome rather than starting a second, overlapping sweep.
                return new PriceRefreshCycleResult(PriceRefreshOutcome.CooldownActive, null, [], 0);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var detached = scope.ServiceProvider.GetRequiredService<PriceRefreshService>();
                    await detached.RunCycleAsync(force: true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Detached manual price refresh sweep failed");
                }
                finally
                {
                    manualRefreshGate.Exit();
                }
            }, CancellationToken.None);

            return new PriceRefreshCycleResult(PriceRefreshOutcome.Queued, null, [], 0);
        }

        return await RunCycleAsync(force: true, cancellationToken);
    }

    /// <summary>True only when the Twelve Data group would actually be fetched this cycle (NYSE
    /// open) and has more active symbols than fit in the credit throttle's single per-minute
    /// chunk — i.e. exactly the case where <c>TwelveDataQuoteProvider.GetQuotesAsync</c> would
    /// need more than one paced request and could take minutes.</summary>
    private async Task<bool> NeedsDetachedTwelveDataSweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!calendar.IsOpen(Market.Nyse, now))
        {
            return false;
        }

        var twelveDataCount = await db.Assets.CountAsync(
            a => a.IsActive && a.QuoteProviderKind == QuoteProviderKind.TwelveData, cancellationToken);

        return twelveDataCount > TwelveDataCreditPolicy.PerMinuteCreditLimit;
    }

    private async Task<PriceRefreshCycleResult> RunCycleAsync(bool force, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var assets = await db.Assets.Where(a => a.IsActive).ToListAsync(cancellationToken);
        var groups = assets.GroupBy(a => a.QuoteProviderKind).ToList();

        var outcomes = new List<SourceRefreshOutcome>();
        var totalSymbolsRefreshed = 0;

        foreach (var group in groups)
        {
            var source = group.Key;
            var market = ProviderMarkets.For(source);

            // Never spend a provider credit polling a closed equity market — not even on a
            // manual trigger. Crypto has no market entry here, so it is never gated.
            if (market is { } gatedMarket && !calendar.IsOpen(gatedMarket, now))
            {
                await statusStore.RecordOutcomeAsync(
                    new SourceRefreshOutcome(source, Attempted: false, Success: true, SymbolsRefreshed: 0, Error: null),
                    now,
                    now + options.Value.StockClosedInterval,
                    cancellationToken);
                continue;
            }

            var nextDueAt = await statusStore.GetNextDueAtAsync(source, cancellationToken);
            var due = force || nextDueAt is null || now >= nextDueAt;
            if (!due)
            {
                continue;
            }

            var groupAssets = group.ToList();
            var outcome = await RefreshGroupAsync(source, groupAssets, now, cancellationToken);

            var interval = await NextIntervalAsync(source, market, groupAssets.Count, cancellationToken);
            await statusStore.RecordOutcomeAsync(outcome, now, now + interval, cancellationToken);

            outcomes.Add(outcome);
            totalSymbolsRefreshed += outcome.SymbolsRefreshed;
        }

        // Nothing ran. A scheduled tick simply returns — recording a row every PollInterval would
        // flood the audit trail with no-ops. A *manual* trigger still records its run, because the
        // cooldown in RefreshNowAsync is derived from persisted manual RefreshRuns: skipping the
        // write whenever every source happened to be gated would leave the endpoint with no
        // cooldown at all and open to being hammered. The outcome reported to the caller is still
        // NothingDue — the run row exists to mark the attempt, not to claim work was done.
        if (outcomes.Count == 0)
        {
            if (force)
            {
                db.AddRefreshRun(BuildRun(force, outcomes, now, totalSymbolsRefreshed));
                await db.SaveChangesAsync(cancellationToken);
            }

            return new PriceRefreshCycleResult(PriceRefreshOutcome.NothingDue, null, [], 0);
        }

        db.AddRefreshRun(BuildRun(force, outcomes, now, totalSymbolsRefreshed));
        await db.SaveChangesAsync(cancellationToken);

        var status = await statusStore.GetSnapshotAsync(
            calendar.IsOpen(Market.Nyse, now),
            calendar.IsOpen(Market.Sgx, now),
            cancellationToken);

        // Enriched with the same PriceRefreshStatusEnricher GET /api/prices/status and the hub's
        // connect handler use, so a broadcast can no longer be the transport that silently sends
        // null for the derived cadence/credit fields (see the enricher's own remarks). This adds
        // one ledger-row lookup and one Assets count per broadcast — both cheap local reads, never
        // a Twelve Data call — in exchange for those three fields no longer flickering null on
        // every push (crypto pushes as often as every two minutes).
        var extended = await statusEnricher.EnrichAsync(status, cancellationToken);
        await broadcaster.BroadcastRefreshStatusAsync(extended, cancellationToken);

        return new PriceRefreshCycleResult(PriceRefreshOutcome.Completed, null, outcomes, totalSymbolsRefreshed);
    }

    /// <summary>The interval until this source is next due. Crypto keeps its fixed
    /// <see cref="PriceRefreshOptions.CryptoInterval"/>; Yahoo (SGX) keeps the fixed
    /// <see cref="PriceRefreshOptions.StockOpenInterval"/> floor since it is free and unmetered
    /// (see the D37/tracker note). Twelve Data alone gets a derived cadence — see D38 and
    /// <see cref="TwelveDataCadenceCalculator"/> — because it alone is credit-limited: a hardcoded
    /// interval is correct for exactly one portfolio size, and this is what stops the cadence
    /// silently under-pacing again as the portfolio grows past today's symbol count.</summary>
    private async Task<TimeSpan> NextIntervalAsync(
        QuoteProviderKind source, Market? market, int groupAssetCount, CancellationToken cancellationToken)
    {
        if (market is null)
        {
            return options.Value.CryptoInterval;
        }

        if (source != QuoteProviderKind.TwelveData)
        {
            return options.Value.StockOpenInterval;
        }

        var creditStatus = await creditThrottle.GetStatusAsync(cancellationToken);
        return TwelveDataCadenceCalculator.DeriveStockOpenInterval(
            groupAssetCount,
            creditStatus.RemainingToday,
            TimeSpan.FromMinutes(TwelveDataCreditPolicy.NyseSessionMinutes),
            options.Value.StockOpenInterval);
    }

    /// <summary>Fetches and upserts one provider's batch. Never throws: a provider exception is
    /// caught here so the group degrades to its last stored quote and the cycle continues with
    /// the remaining groups.</summary>
    private async Task<SourceRefreshOutcome> RefreshGroupAsync(
        QuoteProviderKind source,
        IReadOnlyList<Asset> groupAssets,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<QuoteFetchResult> results;
        try
        {
            var provider = router.GetProvider(groupAssets[0]);
            results = await provider.GetQuotesAsync(groupAssets, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Price refresh failed for provider {Source} ({AssetCount} assets); degrading to last stored quotes",
                source,
                groupAssets.Count);
            return new SourceRefreshOutcome(source, Attempted: true, Success: false, SymbolsRefreshed: 0, Error: ex.Message);
        }

        var succeeded = 0;
        var firstError = (string?)null;

        foreach (var result in results)
        {
            if (!result.Success || result.Price is not { } price || result.Currency is null || result.AsOf is not { } asOf)
            {
                firstError ??= result.Error;
                logger.LogWarning(
                    "Quote fetch failed for asset {AssetId} via {Source}: {Error}",
                    result.AssetId,
                    source,
                    result.Error);
                continue; // degrade to whatever PriceQuote row (if any) is already stored
            }

            await UpsertQuoteAsync(result.AssetId, price, result.Currency, asOf, cancellationToken);
            succeeded++;

            var asset = groupAssets.FirstOrDefault(a => a.Id == result.AssetId);
            if (asset is not null)
            {
                // D4: a successful fetch is not automatically a live price. On an unmodelled SGX
                // lunar holiday this poll should never have happened, and what came back is the
                // previous session's close — so classify it the same way the read path does rather
                // than letting the push be the one place that still says "live" unconditionally.
                await broadcaster.BroadcastQuoteUpdatedAsync(
                    new QuoteUpdateNotification(
                        asset.Id,
                        asset.Symbol,
                        price,
                        result.Currency,
                        asOf,
                        QuoteFreshness.Classify(calendar, asset.QuoteProviderKind, asOf, now)),
                    cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        // Deliberate success rule (this is the fix for the false-success bug found live against
        // the Docker stack — CoinGecko 401ing for every coin in the batch was still reporting
        // LastRunSuccess: true because no .NET exception was thrown):
        //   - Zero successes out of a non-empty batch is a FAILURE. This is what a whole-batch
        //     provider error looks like from here — the HTTP call itself didn't throw (Twelve
        //     Data and CoinGecko both return 200/401 with a per-symbol or whole-batch error body,
        //     not a thrown exception), but every symbol in the group individually failed, so the
        //     cycle accomplished nothing and must say so.
        //   - One or more successes, even with `firstError` populated, is NOT a failure. Twelve
        //     Data nests a per-symbol error inside an otherwise-successful batch response, and a
        //     single bad symbol must not flip the whole provider to "failed" when the rest of the
        //     batch genuinely refreshed — that distinction is preserved from the original code.
        // `Error` still carries `firstError` either way, so a partial failure remains visible in
        // the status payload even though `Success` is true for it.
        var success = succeeded > 0;
        return new SourceRefreshOutcome(source, Attempted: true, Success: success, SymbolsRefreshed: succeeded, Error: firstError);
    }

    private async Task UpsertQuoteAsync(
        int assetId, decimal price, string currency, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var existing = await db.PriceQuotes.FirstOrDefaultAsync(q => q.AssetId == assetId, cancellationToken);
        if (existing is null)
        {
            db.AddPriceQuote(new PriceQuote { AssetId = assetId, Price = price, Currency = currency, AsOf = asOf });
        }
        else
        {
            existing.Price = price;
            existing.Currency = currency;
            existing.AsOf = asOf;
        }
    }

    /// <summary>Builds the durable audit row for one cycle. Valid for an empty outcome list too —
    /// that is the manual-trigger-but-everything-gated case, which records a run with no asset
    /// class, no error and zero symbols so the cooldown still has something to measure from.</summary>
    private RefreshRun BuildRun(
        bool force, IReadOnlyList<SourceRefreshOutcome> outcomes, DateTimeOffset startedAt, int totalSymbolsRefreshed) =>
        new()
        {
            Trigger = force ? RefreshTrigger.Manual : RefreshTrigger.Scheduled,
            AssetClass = InferAssetClass(outcomes),
            StartedAt = startedAt,
            CompletedAt = timeProvider.GetUtcNow(),
            Success = outcomes.All(o => o.Success),
            ErrorMessage = BuildErrorSummary(outcomes),
            SymbolsRefreshed = totalSymbolsRefreshed,
        };

    private static AssetClass? InferAssetClass(IReadOnlyList<SourceRefreshOutcome> outcomes)
    {
        var classes = outcomes
            .Select(o => o.Source == QuoteProviderKind.CoinGecko ? AssetClass.Crypto : AssetClass.Stock)
            .Distinct()
            .ToList();

        return classes.Count == 1 ? classes[0] : null;
    }

    private static string? BuildErrorSummary(IReadOnlyList<SourceRefreshOutcome> outcomes)
    {
        var errors = outcomes
            .Where(o => o.Error is not null)
            .Select(o => $"{o.Source}: {o.Error}")
            .ToList();

        return errors.Count == 0 ? null : string.Join("; ", errors);
    }
}
