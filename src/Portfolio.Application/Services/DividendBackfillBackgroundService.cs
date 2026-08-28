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

                if (result is { Outcome: DividendBackfillOutcome.Completed, Summary: { } summary })
                {
                    logger.LogInformation(
                        "Scheduled dividend backfill completed: {AssetsProcessed} asset(s) processed, {EventsInserted} dividend event(s) inserted, {CallsUsed} provider call(s) used",
                        summary.AssetsProcessed.Count,
                        summary.DividendEventsInserted,
                        summary.ProviderCallsUsed);
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
