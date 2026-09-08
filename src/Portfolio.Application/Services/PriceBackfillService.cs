using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services.Calculators;
using Portfolio.Application.Services.Calendar;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// See <see cref="IPriceBackfillService"/>. Reporting currency is USD, so only currencies other
/// than USD need an FX history (today that is just SGD, for Z74) — the FX pair(s) needed are
/// derived from the currencies of assets that actually have transactions, not hard-coded.
///
/// Stocks only, by decision — crypto is gain/loss only and keeps no <see cref="PriceHistory"/> at
/// all, so backfilling it would spend Twelve Data/CoinGecko rate limit on data nothing reads.
///
/// <para><b>D47: due-ness and the run itself are scoped per <see cref="Market"/>, not global.</b>
/// The bug this closed: NYSE and SGX close at very different local times but were gated by ONE
/// NYSE-keyed "is the market open" check plus ONE once-per-Singapore-day latch, applied to every
/// asset. NYSE closing at ~04:00 SGT satisfied both gates for the whole run, including Z74 — but
/// SGX's own session for that same calendar day had not even opened yet at 04:00 SGT, so Z74 only
/// ever got the PREVIOUS day's close at that point. By the time SGX actually published its close
/// (17:00 SGT), the once-per-day latch had already fired (from the 04:00 run) and blocked a
/// second attempt — and NYSE reopening at 21:30 SGT then blocked everything again until the next
/// 04:00. Z74's close landed roughly 11 hours late, every single day, with 100% uptime — a
/// structural gating bug, not a downtime symptom. The read path
/// (<c>PortfolioSummaryService</c>'s live/close fallback) was never wrong; the close it fell back
/// to just usually was not on file yet. The fix gates each market on its OWN session and its OWN
/// last close (<see cref="IMarketCalendar.LastSessionCloseAt"/>) rather than NYSE's or a shared
/// calendar-day latch — see <c>RunIfDueAsync</c> and tracker.md's D47 entry.</para>
/// </summary>
public sealed class PriceBackfillService(
    IPortfolioDbContext db,
    IQuoteProviderRouter router,
    IFxRateProvider fxRateProvider,
    IMarketCalendar calendar,
    TimeProvider timeProvider,
    IOptions<PriceBackfillOptions> options,
    ITwelveDataCreditThrottle creditThrottle,
    ILogger<PriceBackfillService> logger) : IPriceBackfillService
{
    private const string ReportingCurrency = "USD";

    public async Task<PriceBackfillRunResult> RunIfDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var dueMarkets = new List<Market>();
        var skipped = new List<PriceBackfillMarketSkip>();

        foreach (var market in ProviderMarkets.All)
        {
            // Don't spend a call mid-session for THIS market — its own day's close is not on the
            // wire yet. NYSE being open must never gate SGX's due-ness, and vice versa (D47):
            // each market is evaluated entirely on its own session.
            if (calendar.IsOpen(market, now))
            {
                skipped.Add(new PriceBackfillMarketSkip(market, PriceBackfillSkipReason.SessionOpen));
                continue;
            }

            // A market publishes exactly one close per session, so "has a scheduled run completed
            // for this market since ITS OWN last close?" is both the due-ness check and, for free,
            // the once-per-session throttle the old global once-per-day latch used to provide
            // separately (and wrongly — see the class remarks). Self-correcting after downtime: on
            // startup, any market closed and uncovered since its own last close is immediately due.
            var lastClose = calendar.LastSessionCloseAt(market, now);
            var lastRunCompletedAt = await db.RefreshRuns
                .Where(r => r.Trigger == RefreshTrigger.BackfillScheduled && r.Market == market)
                .MaxAsync(r => (DateTimeOffset?)r.CompletedAt, cancellationToken);

            if (lastRunCompletedAt is { } completed && completed >= lastClose)
            {
                skipped.Add(new PriceBackfillMarketSkip(market, PriceBackfillSkipReason.AlreadyCoveredSinceLastClose));
                continue;
            }

            dueMarkets.Add(market);
        }

        if (dueMarkets.Count == 0)
        {
            return new PriceBackfillRunResult([], skipped, null);
        }

        var summary = await RunAsync(RefreshTrigger.BackfillScheduled, dueMarkets, cancellationToken);
        return new PriceBackfillRunResult(dueMarkets, skipped, summary);
    }

    public async Task<PriceBackfillSummary> RunAsync(
        RefreshTrigger trigger, IReadOnlyCollection<Market> markets, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = ReportingClock.Today(timeProvider);
        var marketSet = markets.ToHashSet();

        // D37: the budget is derived from what Twelve Data's persisted daily ledger says is
        // actually left today, not a hardcoded constant — a fixed budget smaller than one full
        // pass (20, against the 22 calls 21 assets + 1 FX pair needed) is exactly what left a
        // contiguous block of assets permanently unbackfilled. MaxProviderCallsPerRun still acts
        // as an explicit safety ceiling for a single run (its default no longer artificially
        // constrains below the derived figure — see PriceBackfillOptions).
        var creditStatus = await creditThrottle.GetStatusAsync(cancellationToken);
        var budget = Math.Min(options.Value.MaxProviderCallsPerRun, creditStatus.RemainingToday);
        var callsUsed = 0;

        var assetsProcessed = new List<string>();
        var assetsSkippedForBudget = new List<string>();
        var assetsFailed = new List<AssetBackfillFailure>();
        var assetsSkippedTodayNotClosed = new List<string>();
        var assetsTruncated = new List<string>();
        var priceHistoryInserted = 0;
        var fxRateInserted = 0;

        // Crypto is gain/loss only, by decision — it keeps no PriceHistory at all, so backfilling
        // it would spend provider rate limit on data nothing reads. See the Phase 6 decision.
        //
        // D47: further filtered to only the assets whose OWN market (via ProviderMarkets, the
        // single provider-to-market table — see its own doc comment and D7) is in scope for this
        // run. This is what makes an SGX-only run spend zero Twelve Data credits on NYSE assets —
        // they are never even loaded into the ordering/FX derivation below, not merely skipped
        // after the fact.
        var assets = await db.Assets
            .Where(a => a.IsActive && a.AssetClass == AssetClass.Stock)
            .ToListAsync(cancellationToken);

        assets = assets
            .Where(a => ProviderMarkets.For(a.QuoteProviderKind) is { } assetMarket && marketSet.Contains(assetMarket))
            .ToList();

        var marketByAssetSymbol = assets.ToDictionary(
            a => a.Symbol,
            a => ProviderMarkets.For(a.QuoteProviderKind)!.Value);

        // D37: least-recently-backfilled first (nulls — never backfilled at all — first of all),
        // not the implicit clustered-index (Id) order EF/SQL Server return with no ORDER BY. That
        // stable ordering is exactly what made a budget-truncated run starve the SAME contiguous
        // block of assets on every single run, forever: with no ORDER BY, the query always came
        // back in Id order, so a budget of N always served the same first N assets and never
        // reached the rest. Sorting by "how stale is this asset's history" instead means whichever
        // assets fell short of the budget last time are first in line next time.
        var lastBackfilledByAsset = await db.PriceHistories
            .GroupBy(p => p.AssetId)
            .Select(g => new { AssetId = g.Key, LastDate = g.Max(p => p.Date) })
            .ToDictionaryAsync(x => x.AssetId, x => x.LastDate, cancellationToken);

        assets = assets
            .OrderBy(a => lastBackfilledByAsset.TryGetValue(a.Id, out var lastDate) ? lastDate : DateOnly.MinValue)
            .ThenBy(a => a.Id) // stable, deterministic tie-break for assets backfilled on the same date
            .ToList();

        var earliestTradeDateByAsset = await db.Transactions
            .GroupBy(t => t.AssetId)
            .Select(g => new { AssetId = g.Key, Earliest = g.Min(t => t.TradeDate) })
            .ToDictionaryAsync(x => x.AssetId, x => x.Earliest, cancellationToken);

        // Derived up front, before either loop spends a call, so the FX loop below can run first
        // without waiting on the asset loop to discover which currencies are in play. Derived from
        // the already market-filtered `assets`, so an SGX-only run only ever asks for USD/SGD, not
        // any currency pair only an out-of-scope NYSE asset would need.
        var currenciesNeedingFx = assets
            .Where(a => earliestTradeDateByAsset.ContainsKey(a.Id) && a.Currency != ReportingCurrency)
            .Select(a => a.Currency)
            .ToHashSet();

        // FX runs before the per-asset price-history loop, and gets first claim on the shared
        // call budget, even though it appears second in PriceBackfillSummary's field order. This
        // is deliberate, not an oversight: a missing FX rate throws inside FxRateResolver and 500s
        // every USD-reporting endpoint for every holding, USD-denominated or not, while a missing
        // price-history point just leaves a gap in one asset's chart. FX is a hard prerequisite;
        // price history is degradable. Running the asset loop first (as this used to) let it burn
        // the entire budget on Twelve Data's 8-req/min limit before FX ever got a turn, so the one
        // FX call at the end was reliably 429'd once more than ~8 stocks were held.
        foreach (var currency in currenciesNeedingFx)
        {
            // The earliest date any asset in this currency needs a converted value.
            var from = assets
                .Where(a => a.Currency == currency && earliestTradeDateByAsset.ContainsKey(a.Id))
                .Select(a => earliestTradeDateByAsset[a.Id])
                .DefaultIfEmpty(today)
                .Min();

            if (from == today)
            {
                // Same reasoning as the per-asset guard below: a same-day range cannot succeed, so
                // do not spend a call finding that out.
                assetsSkippedTodayNotClosed.Add($"FX:{ReportingCurrency}/{currency}");
                continue;
            }

            if (callsUsed >= budget)
            {
                assetsSkippedForBudget.Add($"FX:{ReportingCurrency}/{currency}");
                continue;
            }

            FxHistoryFetchResult fxResult;
            try
            {
                fxResult = await fxRateProvider.GetHistoryAsync(ReportingCurrency, currency, from, today, cancellationToken);
                callsUsed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Historical FX backfill failed for {Base}/{Quote}",
                    ReportingCurrency,
                    currency);
                assetsFailed.Add(new AssetBackfillFailure($"FX:{ReportingCurrency}/{currency}", ex.Message));
                continue;
            }

            if (!fxResult.Success)
            {
                // Must land in assetsFailed, not silently pass through as zero rates inserted —
                // this is the false-success bug: a 429 that returns an empty list here is
                // indistinguishable from "the provider genuinely has no rates for this range"
                // unless the failure is surfaced explicitly.
                logger.LogWarning(
                    "Historical FX backfill for {Base}/{Quote} failed: {Error}",
                    ReportingCurrency,
                    currency,
                    fxResult.Error);
                assetsFailed.Add(new AssetBackfillFailure(
                    $"FX:{ReportingCurrency}/{currency}", fxResult.Error ?? "Provider reported failure without a message."));
                continue;
            }

            var existingFxDates = await db.FxRates
                .Where(f => f.Base == ReportingCurrency && f.Quote == currency && f.Date >= from && f.Date <= today)
                .Select(f => f.Date)
                .ToListAsync(cancellationToken);
            var existingFxDateSet = existingFxDates.ToHashSet();

            foreach (var point in fxResult.Points)
            {
                if (!existingFxDateSet.Add(point.Date))
                {
                    continue;
                }

                db.AddFxRate(new FxRate
                {
                    Date = point.Date,
                    Base = ReportingCurrency,
                    Quote = currency,
                    Rate = point.Rate,
                });
                fxRateInserted++;
            }
        }

        foreach (var asset in assets)
        {
            if (!earliestTradeDateByAsset.TryGetValue(asset.Id, out var from))
            {
                // No transactions yet for this asset — nothing to backfill against.
                continue;
            }

            if (from == today)
            {
                // The requested range collapses to today alone, and an equity provider cannot
                // return a daily close for a session that has not finished — Twelve Data returns
                // HTTP 400 for exactly this, every time, regardless of budget. Short-circuit before
                // spending a call: raising MaxProviderCallsPerRun would not fix a guaranteed
                // failure, so this must never be reported as a budget skip (or a failure — it isn't
                // one, it is simply premature). A later run, once "today" has become a past date,
                // will pick this asset up with a normal multi-day range.
                logger.LogInformation(
                    "Historical price backfill deferred for asset {AssetId} ({Symbol}): earliest trade date is today, no close published yet",
                    asset.Id,
                    asset.Symbol);
                assetsSkippedTodayNotClosed.Add(asset.Symbol);
                continue;
            }

            if (callsUsed >= budget)
            {
                assetsSkippedForBudget.Add(asset.Symbol);
                continue;
            }

            HistoryFetchResult historyResult;
            try
            {
                var provider = router.GetProvider(asset);
                historyResult = await provider.GetHistoryAsync(asset, from, today, cancellationToken);
                callsUsed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Historical price backfill failed for asset {AssetId} ({Symbol})",
                    asset.Id,
                    asset.Symbol);
                assetsFailed.Add(new AssetBackfillFailure(asset.Symbol, ex.Message));
                continue;
            }

            if (!historyResult.Success)
            {
                logger.LogWarning(
                    "Historical price backfill for asset {AssetId} ({Symbol}) failed: {Error}",
                    asset.Id,
                    asset.Symbol,
                    historyResult.Error);
                assetsFailed.Add(new AssetBackfillFailure(asset.Symbol, historyResult.Error ?? "Provider reported failure without a message."));
                continue;
            }

            if (historyResult.Truncated)
            {
                // The series starts later than requested by provider policy, not by accident —
                // record it so a caller (and eventually Phase 6's cost-vs-market-value series)
                // can tell the two apart instead of silently building on a gap.
                logger.LogWarning(
                    "Historical price backfill for asset {AssetId} ({Symbol}) truncated: requested from {RequestedFrom}, provider limited to {EffectiveFrom}",
                    asset.Id,
                    asset.Symbol,
                    historyResult.RequestedFrom,
                    historyResult.EffectiveFrom);
                assetsTruncated.Add(
                    $"{asset.Symbol}: requested from {historyResult.RequestedFrom:yyyy-MM-dd}, provider limited to {historyResult.EffectiveFrom:yyyy-MM-dd}");
            }

            var existingDates = await db.PriceHistories
                .Where(p => p.AssetId == asset.Id && p.Date >= historyResult.EffectiveFrom && p.Date <= today)
                .Select(p => p.Date)
                .ToListAsync(cancellationToken);
            var existingDateSet = existingDates.ToHashSet();

            foreach (var point in historyResult.Points)
            {
                if (!existingDateSet.Add(point.Date))
                {
                    continue;
                }

                db.AddPriceHistory(new PriceHistory
                {
                    AssetId = asset.Id,
                    Date = point.Date,
                    Close = point.Close,
                    Currency = point.Currency,
                });
                priceHistoryInserted++;
            }

            assetsProcessed.Add(asset.Symbol);
        }

        // Audit row alongside the quote-refresh RefreshRuns (see RefreshTrigger) — one row PER
        // MARKET covered by this run (D47), not one row for the whole run, so RunIfDueAsync's
        // per-market due-ness query (Trigger + Market) has something to read for each exchange
        // independently. SymbolsRefreshed is counted per market (an asset's own processed/failed
        // outcome is shared across the run, but how many symbols a given market's row can claim
        // credit for is market-specific — see D45's reminder that internal bookkeeping numbers
        // need the same "which one actually happened" care as outward-facing DTOs).
        var runSummaryText = BuildRunSummary(assetsSkippedForBudget, assetsFailed, assetsSkippedTodayNotClosed, assetsTruncated);
        var completedAt = timeProvider.GetUtcNow();
        foreach (var market in markets)
        {
            var symbolsForMarket = assetsProcessed.Count(symbol =>
                marketByAssetSymbol.TryGetValue(symbol, out var assetMarket) && assetMarket == market);

            db.AddRefreshRun(new RefreshRun
            {
                Trigger = trigger,
                AssetClass = AssetClass.Stock,
                Market = market,
                StartedAt = now,
                CompletedAt = completedAt,
                Success = assetsFailed.Count == 0,
                ErrorMessage = runSummaryText,
                SymbolsRefreshed = symbolsForMarket,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        return new PriceBackfillSummary(
            assetsProcessed,
            assetsSkippedForBudget,
            assetsFailed,
            assetsSkippedTodayNotClosed,
            priceHistoryInserted,
            fxRateInserted,
            callsUsed,
            assetsTruncated);
    }

    /// <summary>Best-effort backfill has no single pass/fail flag — a skipped or truncated asset
    /// is not an error, just something worth a human noticing in the audit trail. Kept separated
    /// by reason, not merged into one bag of strings, for the same reason the summary DTO keeps
    /// them separate: "budget", "failed", and "today, not closed yet" call for different human
    /// responses.</summary>
    private static string? BuildRunSummary(
        IReadOnlyList<string> skippedForBudget,
        IReadOnlyList<AssetBackfillFailure> failed,
        IReadOnlyList<string> skippedTodayNotClosed,
        IReadOnlyList<string> truncated)
    {
        var parts = new List<string>();
        if (skippedForBudget.Count > 0)
        {
            parts.Add($"{skippedForBudget.Count} skipped for budget: {string.Join(", ", skippedForBudget)}");
        }

        if (failed.Count > 0)
        {
            parts.Add($"{failed.Count} failed: {string.Join(", ", failed.Select(f => $"{f.Symbol} ({f.Error})"))}");
        }

        if (skippedTodayNotClosed.Count > 0)
        {
            parts.Add($"{skippedTodayNotClosed.Count} deferred (today not closed yet): {string.Join(", ", skippedTodayNotClosed)}");
        }

        if (truncated.Count > 0)
        {
            parts.Add($"{truncated.Count} truncated: {string.Join(", ", truncated)}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
