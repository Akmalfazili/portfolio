using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Portfolio.Application.Services;

/// <summary>
/// Thin hosting wrapper around <see cref="IDividendBackfillService.RunIfDueAsync"/>, modelled
/// directly on <see cref="PriceBackfillBackgroundService"/>: a fresh DI scope per tick, and a
/// single bad tick is logged and swallowed rather than allowed to end the loop, or the app would
/// silently stop backfilling dividends until the next restart.
/// </summary>
public sealed class DividendBackfillBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<DividendBackfillOptions> options,
    ILogger<DividendBackfillBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var backfillService = scope.ServiceProvider.GetRequiredService<IDividendBackfillService>();
                var result = await backfillService.RunIfDueAsync(stoppingToken);

                // D51: a retry is logged distinctly from a full run, not folded into the same
                // "completed" message — a retry's AssetsProcessed count is scoped to only the
                // previously-failed assets, and reading it as "N assets checked today" would
                // understate the day's real coverage (the D10/D26/D33/D35/D38/D45/D47 family of
                // mistake, one layer down: collapsing two different outcomes into one label).
                if (result is { Outcome: DividendBackfillOutcome.Completed, Summary: { } summary })
                {
                    logger.LogInformation(
                        "Scheduled dividend backfill completed: {AssetsProcessed} asset(s) processed, {EventsInserted} dividend event(s) inserted, {CallsUsed} provider call(s) used",
                        summary.AssetsProcessed.Count,
                        summary.DividendEventsInserted,
                        summary.ProviderCallsUsed);
                }
                else if (result is { Outcome: DividendBackfillOutcome.RetryCompleted, Summary: { } retrySummary })
                {
                    logger.LogInformation(
                        "Scheduled dividend backfill retried {AssetsRetried} previously-failed asset(s): {AssetsProcessed} succeeded, {EventsInserted} dividend event(s) inserted, {CallsUsed} provider call(s) used",
                        retrySummary.AssetsProcessed.Count + retrySummary.AssetsFailed.Count + retrySummary.AssetsSkippedForBudget.Count,
                        retrySummary.AssetsProcessed.Count,
                        retrySummary.DividendEventsInserted,
                        retrySummary.ProviderCallsUsed);
                }
                else if (result.Outcome == DividendBackfillOutcome.RetryPending)
                {
                    // D51 follow-up: an all-assets-failed run is waiting out FailedAssetRetryInterval
                    // before its next full retry — logged at Debug (not Warning) since this is the
                    // pacing working as designed, not a new failure; the original failure was already
                    // logged when it happened.
                    logger.LogDebug("Scheduled dividend backfill not due: the previous run failed outright and is still within its retry pacing window");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled dividend backfill check failed unexpectedly; will retry on the next poll tick");
            }

            try
            {
                await Task.Delay(options.Value.SchedulePollInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
