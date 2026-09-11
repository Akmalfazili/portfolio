using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services.Calculators;
using Portfolio.Domain.Entities;
using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>
/// See <see cref="IDividendBackfillService"/>. Stocks only, by decision — crypto pays no dividends
/// and is out of scope entirely, so it is filtered out identically to how
/// <c>PriceBackfillService</c> filters crypto out of <c>PriceHistory</c> backfilling.
///
/// <para><b>D51: a partial failure used to latch the whole reporting day.</b> Before D51,
/// <see cref="RunIfDueAsync"/>'s only gate was "did a productive scheduled run happen today" — a
/// run where every asset but one succeeded still had <c>SymbolsRefreshed &gt; 0</c> and counted as
/// fully covering the day, stranding the one failed asset until tomorrow's reporting day even
/// though Yahoo is free and cheap to retry. <see cref="RunIfDueAsync"/> now also checks, once a
/// full run has happened today, for any asset whose <see cref="AssetDividendState.LastRunSuccess"/>
/// is false and whose <see cref="AssetDividendState.LastAttemptedAt"/> is old enough
/// (<see cref="DividendBackfillOptions.FailedAssetRetryInterval"/>), and retries exactly those via
/// <see cref="RunAsyncCore"/>'s <c>restrictToAssetIds</c> — never a second full run just to recheck
/// 21 assets that already succeeded.</para>
///
/// <para><b>D51 follow-up #1 (found on review, before deploy): the all-assets-failed case bypassed
/// the same pacing.</b> An all-failed full run ALSO has <c>SymbolsRefreshed == 0</c> — the exact
/// shape D41's genuinely-nothing-to-do zero-asset case has, which must keep retrying immediately
/// with no delay. Without a way to tell the two apart, an all-failed run fell through to the
/// unpaced D41 branch and repeated a FULL run on every single poll tick — and D51 had just lowered
/// <see cref="DividendBackfillOptions.SchedulePollInterval"/> from 1h to 15min to make the
/// partial-failure retry reachable, which made this branch's mistake worse, not better: a
/// sustained Yahoo outage (or rate-limit — exactly when hammering it hurts most, and Z74's live
/// quotes share that endpoint) now got hammered every 15 minutes instead of every hour. Checking
/// <c>Success</c>, not just <c>SymbolsRefreshed</c>, closed this: an all-failed run
/// (<c>SymbolsRefreshed == 0 &amp;&amp; !Success</c>) is paced by the same
/// <see cref="DividendBackfillOptions.FailedAssetRetryInterval"/> the partial-failure retry uses,
/// while a genuinely-empty run (<c>SymbolsRefreshed == 0 &amp;&amp; Success</c>) still retries with
/// no delay at all, exactly as D41 requires.</para>
///
/// <para><b>D51 follow-up #2 (found on the next review, before deploy): a failed NARROWED retry
/// could itself be misread as a fresh all-failed FULL run and escalate.</b> Follow-up #1 keyed its
/// decision off "the single most recent scheduled run" — but a narrowed per-asset retry (the branch
/// just below) writes its own <see cref="Domain.Entities.RefreshRun"/> row, and if every asset in
/// that NARROWED set fails again (e.g. one persistently delisted symbol Yahoo 404s on forever), that
/// row reads <c>SymbolsRefreshed == 0 &amp;&amp; !Success</c> too — structurally indistinguishable
/// from follow-up #1's all-failed FULL run once it becomes the newest row. Read that way, it
/// escalated into a full 22-asset run every <see cref="DividendBackfillOptions.FailedAssetRetryInterval"/>,
/// all day, for one bad symbol — the same hammering follow-up #1 had just closed, reopened through a
/// different door. <see cref="RunIfDueAsync"/> now decides "has a PRODUCTIVE run happened today" — a
/// question independent of which run is most recent — before looking at anything else: once true, it
/// stays true for the rest of the reporting day (the original productive row never disappears), so
/// every later check stays on the narrowed retry path and a full run is never triggered again until
/// the next reporting day.</para>
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
        // Reporting-day gate, not a UTC one — see ReportingClock. Both sides of the comparison
        // below (the stored StartedAt instant and "today") must move together onto the same
        // Singapore calendar day, or the gate compares two different clocks.
        var today = ReportingClock.Today(timeProvider);

        // Whether a PRODUCTIVE (SymbolsRefreshed > 0) scheduled run has happened today — decided
        // FIRST, and independently of which run is most recent. See the class remarks (D51
        // escalation follow-up): a narrowed per-asset retry (triggered from the branch below) writes
        // its OWN RefreshRun row, and that row can itself read SymbolsRefreshed == 0 && !Success if
        // every asset in the narrowed set fails again (e.g. one persistently-delisted symbol). If
        // that narrowed-retry row were read as "the most recent run" without first checking whether
        // a productive run already happened today, it would be indistinguishable from a fresh
        // ALL-failed FULL run and escalate into re-fetching all 22 assets — the exact Yahoo-hammering
        // D51's own follow-up fix just closed, reopened through a different door. Once a productive
        // run has happened today, this stays true for the rest of the day regardless of how many
        // narrowed-retry rows get written afterward (the original productive row never disappears),
        // so every later check in the same reporting day correctly stays on the narrowed retry path
        // below and never falls back to a full run.
        var lastProductiveScheduledRunAt = await db.RefreshRuns
            .Where(r => r.Trigger == RefreshTrigger.DividendBackfillScheduled && r.SymbolsRefreshed > 0)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => (DateTimeOffset?)r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastProductiveScheduledRunAt is { } lastProductive && ReportingClock.DateFor(lastProductive) == today)
        {
            // D51: a productive full run already happened today — but check for assets whose last
            // attempt failed and are now due for a narrowly-scoped retry, rather than declaring the
            // whole day covered. Yahoo is free and keyless, so retrying just the failed assets costs
            // nothing but a request; there is deliberately no retry-count cap here (contrast the
            // price backfill's MaxFailedRunRetriesPerClose), since there is no credit budget to
            // protect — but the narrowing itself is what keeps a persistently failing symbol from
            // ever escalating back into a full run (see the class remarks).
            var retryDeadline = now - options.Value.FailedAssetRetryInterval;

            var assetIdsWithTransactions = (await db.Transactions
                .Select(t => t.AssetId)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet();

            var stockAssetIds = (await db.Assets
                .Where(a => a.IsActive && a.AssetClass == AssetClass.Stock)
                .Select(a => a.Id)
                .ToListAsync(cancellationToken))
                .ToHashSet();

            var failedAssetIds = await db.AssetDividendStates
                .Where(s => s.LastRunSuccess == false && s.LastAttemptedAt != null && s.LastAttemptedAt <= retryDeadline)
                .Select(s => s.AssetId)
                .ToListAsync(cancellationToken);

            var retryAssetIds = failedAssetIds
                .Where(id => stockAssetIds.Contains(id) && assetIdsWithTransactions.Contains(id))
                .ToHashSet();

            if (retryAssetIds.Count == 0)
            {
                return new DividendBackfillRunResult(DividendBackfillOutcome.AlreadyRanToday, null);
            }

            var retrySummary = await RunAsyncCore(RefreshTrigger.DividendBackfillScheduled, retryAssetIds, cancellationToken);
            return new DividendBackfillRunResult(DividendBackfillOutcome.RetryCompleted, retrySummary);
        }

        // No productive run has happened today. The most recent scheduled run today, if any, is
        // therefore guaranteed to ALSO have SymbolsRefreshed == 0 (otherwise the check above would
        // have been true) — either a genuinely empty D41 run (Success == true — a fresh portfolio
        // with no stock transactions yet, must retry immediately with no delay) or an ALL-failed
        // full run (Success == false, paced by FailedAssetRetryInterval before retrying in full).
        var mostRecentScheduledRunToday = await db.RefreshRuns
            .Where(r => r.Trigger == RefreshTrigger.DividendBackfillScheduled)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (mostRecentScheduledRunToday is { } recent
            && ReportingClock.DateFor(recent.StartedAt) == today
            && !recent.Success
            && now - recent.StartedAt < options.Value.FailedAssetRetryInterval)
        {
            return new DividendBackfillRunResult(DividendBackfillOutcome.RetryPending, null);
        }

        var fullSummary = await RunAsyncCore(RefreshTrigger.DividendBackfillScheduled, restrictToAssetIds: null, cancellationToken);
        return new DividendBackfillRunResult(DividendBackfillOutcome.Completed, fullSummary);
    }

    /// <summary>Unconditional entry point — always a full run over every in-scope stock asset. See
    /// <see cref="IDividendBackfillService"/>. D51's narrowly-scoped retry path is reached only from
    /// <see cref="RunIfDueAsync"/>, via the private <see cref="RunAsyncCore"/> overload below; this
    /// public method never narrows, by design (the manual endpoint must stay a full run,
    /// unchanged).</summary>
    public Task<DividendBackfillSummary> RunAsync(RefreshTrigger trigger, CancellationToken cancellationToken) =>
        RunAsyncCore(trigger, restrictToAssetIds: null, cancellationToken);

    /// <summary>
    /// D51: <paramref name="restrictToAssetIds"/>, when non-null, narrows the run to exactly those
    /// asset ids (a retry) — <c>null</c> means every in-scope stock asset (a full run, the only
    /// behaviour that existed before D51). An empty-but-non-null set is a caller error (the
    /// <c>RunIfDueAsync</c> retry path never calls this with one — it returns
    /// <see cref="DividendBackfillOutcome.AlreadyRanToday"/> instead), so it is not special-cased.
    /// </summary>
    private async Task<DividendBackfillSummary> RunAsyncCore(
        RefreshTrigger trigger, IReadOnlySet<int>? restrictToAssetIds, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = ReportingClock.Today(timeProvider);

        var assetsProcessed = new List<string>();
        var assetsSkippedForBudget = new List<string>();
        var assetsFailed = new List<DividendAssetBackfillFailure>();
        var dividendEventsInserted = 0;
        var callsUsed = 0;

        // Crypto is out of scope entirely — see the class remarks.
        var assets = await db.Assets
            .Where(a => a.IsActive && a.AssetClass == AssetClass.Stock)
            .ToListAsync(cancellationToken);

        if (restrictToAssetIds is not null)
        {
            // D51: a retry touches only the assets that actually need one — never even loaded into
            // the ordering/budget bookkeeping below, the same "narrowed before the loop, not
            // filtered after the fact" shape D47/D51 established for the price backfill's per-market
            // scoping.
            assets = assets.Where(a => restrictToAssetIds.Contains(a.Id)).ToList();
        }

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
