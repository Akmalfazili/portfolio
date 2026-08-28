using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Portfolio.Application.Services;
using Portfolio.Domain.Enums;

namespace Portfolio.Api.Endpoints;

/// <summary>
/// Manual dividend-backfill trigger. The asset-scoped read (<c>GET /api/assets/{id}/dividends</c>)
/// lives in <see cref="AssetsEndpoints"/> alongside <c>/performance</c>; this is a portfolio-wide
/// action, so it gets its own resource group instead of being folded into
/// <see cref="PricesEndpoints"/> — dividends are not a price, and the two backfills' in-flight
/// gates are deliberately independent (see <see cref="ManualDividendBackfillInFlightGate"/>).
/// </summary>
public static class DividendsEndpoints
{
    public static IEndpointRouteBuilder MapDividendEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dividends").WithTags("Dividends");

        // Mirrors POST /api/prices/backfill's own conventions exactly: detached onto a background
        // task so a slow run can never be cancelled by an aborted HTTP connection (misreporting a
        // still-running fetch as a provider failure — the same D37/D38 lesson, applied here even
        // though Yahoo itself is fast and unmetered, purely so the two manual triggers behave
        // consistently), guarded by its own in-flight gate, and returning 202 Accepted promptly.
        // This is also how Defect 1 (a zero-asset scheduled run consuming the day) is recovered by
        // hand rather than by waiting for the next calendar day.
        group.MapPost("/backfill", Results<Accepted<DividendBackfillQueuedResult>, ProblemHttpResult> (
            IServiceScopeFactory scopeFactory,
            ManualDividendBackfillInFlightGate inFlightGate,
            ILogger<Program> logger) =>
        {
            if (!inFlightGate.TryEnter())
            {
                return TypedResults.Problem(
                    title: "Dividend backfill already in progress",
                    detail: "A manually triggered dividend backfill is already running in the background. Try again once it completes.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var backfillService = scope.ServiceProvider.GetRequiredService<IDividendBackfillService>();
                    await backfillService.RunAsync(RefreshTrigger.DividendBackfillManual, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Detached manual dividend backfill failed");
                }
                finally
                {
                    inFlightGate.Exit();
                }
            }, CancellationToken.None);

            return TypedResults.Accepted((string?)null, new DividendBackfillQueuedResult(Queued: true));
        });

        return app;
    }
}
