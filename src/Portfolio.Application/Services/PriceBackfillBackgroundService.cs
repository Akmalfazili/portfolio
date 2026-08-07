using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Dtos;

namespace Portfolio.Application.Services;

/// <summary>
/// Thin hosting wrapper around <see cref="IPriceBackfillService.RunIfDueAsync"/> — see D12.
/// Mirrors <see cref="PriceRefreshBackgroundService"/>'s shape deliberately: a fresh DI scope per
/// tick (<see cref="Abstractions.IPortfolioDbContext"/> is scoped), and a single bad tick is
/// logged and swallowed rather than allowed to end the loop, or the app would silently stop
/// backfilling until the next restart — exactly the failure mode this class exists to close.
///
/// All the actual gating (market-calendar check, once-per-day throttle, the
/// <see cref="PriceBackfillOptions.MaxProviderCallsPerRun"/> budget) lives in
/// <see cref="IPriceBackfillService"/>, not here — this class only knows how often to ask.
/// </summary>
public sealed class PriceBackfillBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<PriceBackfillOptions> options,
    ILogger<PriceBackfillBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var backfillService = scope.ServiceProvider.GetRequiredService<IPriceBackfillService>();
                var result = await backfillService.RunIfDueAsync(stoppingToken);

                if (result is { Outcome: PriceBackfillOutcome.Completed, Summary: { } summary })
                {
                    logger.LogInformation(
                        "Scheduled price backfill completed: {AssetsProcessed} asset(s) processed, {PointsInserted} price history point(s) inserted, {CallsUsed} provider call(s) used",
                        summary.AssetsProcessed.Count,
                        summary.PriceHistoryPointsInserted,
                        summary.ProviderCallsUsed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled price backfill check failed unexpectedly; will retry on the next poll tick");
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
