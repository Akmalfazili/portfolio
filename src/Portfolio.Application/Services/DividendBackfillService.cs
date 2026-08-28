using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// See <see cref="IDividendBackfillService"/>. Stocks only, by decision — crypto pays no dividends
/// and is out of scope entirely, so it is filtered out identically to how
/// <c>PriceBackfillService</c> filters crypto out of <c>PriceHistory</c> backfilling.
/// </summary>
public sealed class DividendBackfillService(
    IPortfolioDbContext db,
    IDividendProvider dividendProvider,
    TimeProvider timeProvider,
    IOptions<DividendBackfillOptions> options,
    ILogger<DividendBackfillService> logger) : IDividendBackfillService
{
    public async Task<DividendBackfillRunResult> RunIfDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // Gates on the most recent scheduled run that actually PROCESSED something
        // (SymbolsRefreshed > 0), not on any scheduled run at all. Found live: RunAsync used to
        // write its RefreshRun unconditionally, even when the stock asset list was empty (a fresh
        // portfolio with no stock transactions yet) — that run accomplished nothing but still
        // consumed the day, locking out the real backfill for up to 24 hours the moment the
        // day's first stock transaction was recorded. Re-running when there is nothing to do is
        // genuinely free (the asset list is empty, so no Yahoo call is made at all), so there is
        // no cost to checking again on the next poll tick.
        //
        // An all-assets-failed run also has SymbolsRefreshed == 0 (only AssetsProcessed counts
        // toward it — see RunAsync), so it falls through the same gate and retries on the next
        // poll rather than waiting a day. That is deliberate, not an oversight: Yahoo is free and
        // keyless, so retrying a failed fetch costs nothing, unlike Twelve Data's credit-limited
        // price backfill where a retry has a real budget cost.
        var lastProductiveScheduledRunAt = await db.RefreshRuns
            .Where(r => r.Trigger == RefreshTrigger.DividendBackfillScheduled && r.SymbolsRefreshed > 0)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => (DateTimeOffset?)r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastProductiveScheduledRunAt is { } last && DateOnly.FromDateTime(last.UtcDateTime) == today)
        {
            return new DividendBackfillRunResult(DividendBackfillOutcome.AlreadyRanToday, null);
        }

        var summary = await RunAsync(RefreshTrigger.DividendBackfillScheduled, cancellationToken);
        return new DividendBackfillRunResult(DividendBackfillOutcome.Completed, summary);
    }

    public async Task<DividendBackfillSummary> RunAsync(RefreshTrigger trigger, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var assetsProcessed = new List<string>();
        var assetsSkippedForBudget = new List<string>();
        var assetsFailed = new List<DividendAssetBackfillFailure>();
        var dividendEventsInserted = 0;
        var callsUsed = 0;

        // Crypto is out of scope entirely — see the class remarks.
        var assets = await db.Assets
            .Where(a => a.IsActive && a.AssetClass == AssetClass.Stock)
            .ToListAsync(cancellationToken);

        var earliestTradeDateByAsset = await db.Transactions
            .GroupBy(t => t.AssetId)
            .Select(g => new { AssetId = g.Key, Earliest = g.Min(t => t.TradeDate) })
            .ToDictionaryAsync(x => x.AssetId, x => x.Earliest, cancellationToken);

        var statesByAsset = await db.AssetDividendStates.ToDictionaryAsync(s => s.AssetId, cancellationToken);

        // D37's anti-starvation ordering, applied the same way here: least-recently-attempted
        // first (never attempted — null — first of all), so a budget-truncated run cannot strand
        // the same tail of assets forever.
        assets = assets
            .Where(a => earliestTradeDateByAsset.ContainsKey(a.Id))
            .OrderBy(a => statesByAsset.TryGetValue(a.Id, out var s) ? s.LastAttemptedAt ?? DateTimeOffset.MinValue : DateTimeOffset.MinValue)
            .ThenBy(a => a.Id)
            .ToList();

        var budget = options.Value.MaxAssetsPerRun;

        foreach (var asset in assets)
        {
            if (callsUsed >= budget)
            {
                assetsSkippedForBudget.Add(asset.Symbol);
                continue;
            }

            var from = earliestTradeDateByAsset[asset.Id];

            Dtos.DividendHistoryFetchResult result;
            try
            {
                result = await dividendProvider.GetDividendHistoryAsync(asset, from, today, cancellationToken);
                callsUsed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Dividend backfill failed for asset {AssetId} ({Symbol})",
                    asset.Id,
                    asset.Symbol);
                RecordState(statesByAsset, asset.Id, now, success: false, ex.Message);
                assetsFailed.Add(new DividendAssetBackfillFailure(asset.Symbol, ex.Message));
                continue;
            }

            if (!result.Success)
            {
                logger.LogWarning(
                    "Dividend backfill for asset {AssetId} ({Symbol}) failed: {Error}",
                    asset.Id,
                    asset.Symbol,
                    result.Error);
                var error = result.Error ?? "Provider reported failure without a message.";
                RecordState(statesByAsset, asset.Id, now, success: false, error);
                assetsFailed.Add(new DividendAssetBackfillFailure(asset.Symbol, error));
                continue;
            }

            var existingExDates = await db.DividendEvents
                .Where(d => d.AssetId == asset.Id)
                .Select(d => d.ExDate)
                .ToListAsync(cancellationToken);
            var existingExDateSet = existingExDates.ToHashSet();

            foreach (var point in result.Points)
            {
                if (!existingExDateSet.Add(point.ExDate))
                {
                    continue;
                }

                db.AddDividendEvent(new DividendEvent
                {
                    AssetId = asset.Id,
                    ExDate = point.ExDate,
                    AmountPerShare = point.AmountPerShare,
                    Currency = point.Currency,
                });
                dividendEventsInserted++;
            }

            RecordState(statesByAsset, asset.Id, now, success: true, error: null);
            assetsProcessed.Add(asset.Symbol);
        }

        db.AddRefreshRun(new RefreshRun
        {
            Trigger = trigger,
            AssetClass = AssetClass.Stock,
            StartedAt = now,
            CompletedAt = timeProvider.GetUtcNow(),
            Success = assetsFailed.Count == 0,
            ErrorMessage = BuildRunSummary(assetsSkippedForBudget, assetsFailed),
            SymbolsRefreshed = assetsProcessed.Count,
        });

        await db.SaveChangesAsync(cancellationToken);

        return new DividendBackfillSummary(
            assetsProcessed, assetsSkippedForBudget, assetsFailed, dividendEventsInserted, callsUsed);
    }

    private void RecordState(
        Dictionary<int, AssetDividendState> statesByAsset, int assetId, DateTimeOffset now, bool success, string? error)
    {
        if (!statesByAsset.TryGetValue(assetId, out var state))
        {
            state = new AssetDividendState { AssetId = assetId };
            db.AddAssetDividendState(state);
            statesByAsset[assetId] = state;
        }

        state.LastAttemptedAt = now;
        state.LastRunSuccess = success;
        state.LastError = error is { Length: > 2000 } ? error[..2000] : error;

        if (success)
        {
            state.LastSuccessAt = now;
        }
    }

    private static string? BuildRunSummary(
        IReadOnlyList<string> skippedForBudget, IReadOnlyList<DividendAssetBackfillFailure> failed)
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

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
