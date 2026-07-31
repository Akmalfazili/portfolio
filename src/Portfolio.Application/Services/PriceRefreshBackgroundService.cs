using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Portfolio.Application.Services;

/// <summary>
/// Thin hosting wrapper around <see cref="IPriceRefreshService"/>. Wakes up every
/// <see cref="PriceRefreshOptions.PollInterval"/> and asks it to refresh whatever is due —
/// <see cref="IPriceRefreshService.RefreshDueAsync"/> owns the actual 5/60/2-minute cadence and
/// the market-calendar gate, so this class knows nothing about either. <see cref="IPortfolioDbContext"/>
/// is scoped (it wraps a scoped <c>DbContext</c>), so a new DI scope is created per tick rather
/// than resolving <see cref="IPriceRefreshService"/> once for the process lifetime.
///
/// A single bad tick — a provider outage, a transient SQL Server hiccup — is logged and the loop
/// keeps going; it must never exit early, or the app silently stops refreshing prices until the
/// next restart.
/// </summary>
public sealed class PriceRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<PriceRefreshOptions> options,
    ILogger<PriceRefreshBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var refreshService = scope.ServiceProvider.GetRequiredService<IPriceRefreshService>();
                await refreshService.RefreshDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled price refresh cycle failed unexpectedly; will retry on the next poll tick");
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
