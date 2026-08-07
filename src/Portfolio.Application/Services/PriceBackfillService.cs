using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
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
/// </summary>
public sealed class PriceBackfillService(
    IPortfolioDbContext db,
    IQuoteProviderRouter router,
    IFxRateProvider fxRateProvider,
    IMarketCalendar calendar,
    TimeProvider timeProvider,
    IOptions<PriceBackfillOptions> options,
    ILogger<PriceBackfillService> logger) : IPriceBackfillService
{
    private const string ReportingCurrency = "USD";

    public async Task<PriceBackfillRunResult> RunIfDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Don't spend a call mid-session — the day's own close is not on the wire yet, and this
        // would just re-fetch yesterday's, which is already in PriceHistory from the last run.
        if (calendar.IsOpen(Market.Nyse, now))
        {
            return new PriceBackfillRunResult(PriceBackfillOutcome.MarketOpen, null);
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var lastScheduledRunAt = await db.RefreshRuns
            .Where(r => r.Trigger == RefreshTrigger.BackfillScheduled)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => (DateTimeOffset?)r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastScheduledRunAt is { } last && DateOnly.FromDateTime(last.UtcDateTime) == today)
        {
            return new PriceBackfillRunResult(PriceBackfillOutcome.AlreadyRanToday, null);
        }

        var summary = await RunAsync(RefreshTrigger.BackfillScheduled, cancellationToken);
        return new PriceBackfillRunResult(PriceBackfillOutcome.Completed, summary);
    }

    public async Task<PriceBackfillSummary> RunAsync(RefreshTrigger trigger, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var budget = options.Value.MaxProviderCallsPerRun;
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
        var assets = await db.Assets
            .Where(a => a.IsActive && a.AssetClass == AssetClass.Stock)
            .ToListAsync(cancellationToken);

        var earliestTradeDateByAsset = await db.Transactions
            .GroupBy(t => t.AssetId)
            .Select(g => new { AssetId = g.Key, Earliest = g.Min(t => t.TradeDate) })
            .ToDictionaryAsync(x => x.AssetId, x => x.Earliest, cancellationToken);

        var currenciesNeedingFx = new HashSet<string>();

        foreach (var asset in assets)
        {
            if (!earliestTradeDateByAsset.TryGetValue(asset.Id, out var from))
            {
                // No transactions yet for this asset — nothing to backfill against.
                continue;
            }

            if (asset.Currency != ReportingCurrency)
            {
                currenciesNeedingFx.Add(asset.Currency);
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
                // Same reasoning as the per-asset guard above: a same-day range cannot succeed, so
                // do not spend a call finding that out.
                assetsSkippedTodayNotClosed.Add($"FX:{ReportingCurrency}/{currency}");
                continue;
            }

            if (callsUsed >= budget)
            {
                assetsSkippedForBudget.Add($"FX:{ReportingCurrency}/{currency}");
                continue;
            }

            IReadOnlyList<FxRatePoint> fxPoints;
            try
            {
                fxPoints = await fxRateProvider.GetHistoryAsync(ReportingCurrency, currency, from, today, cancellationToken);
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

            var existingFxDates = await db.FxRates
                .Where(f => f.Base == ReportingCurrency && f.Quote == currency && f.Date >= from && f.Date <= today)
                .Select(f => f.Date)
                .ToListAsync(cancellationToken);
            var existingFxDateSet = existingFxDates.ToHashSet();

            foreach (var point in fxPoints)
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

        // Audit row alongside the quote-refresh RefreshRuns (see RefreshTrigger), so the manual
        // POST /api/prices/backfill endpoint and the daily scheduled run both leave a durable
        // record — and so RunIfDueAsync's own "already ran today" check has something to read.
        db.AddRefreshRun(new RefreshRun
        {
            Trigger = trigger,
            AssetClass = AssetClass.Stock,
            StartedAt = now,
            CompletedAt = timeProvider.GetUtcNow(),
            Success = assetsFailed.Count == 0,
            ErrorMessage = BuildRunSummary(assetsSkippedForBudget, assetsFailed, assetsSkippedTodayNotClosed, assetsTruncated),
            SymbolsRefreshed = assetsProcessed.Count,
        });

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
