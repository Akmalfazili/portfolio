using Microsoft.EntityFrameworkCore;
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

        return await RunCycleAsync(force: true, cancellationToken);
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

            var interval = market is not null ? options.Value.StockOpenInterval : options.Value.CryptoInterval;
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
        await broadcaster.BroadcastRefreshStatusAsync(status, cancellationToken);

        return new PriceRefreshCycleResult(PriceRefreshOutcome.Completed, null, outcomes, totalSymbolsRefreshed);
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
