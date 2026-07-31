using Microsoft.AspNetCore.Http.HttpResults;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;

namespace Portfolio.Api.Endpoints;

public static class PricesEndpoints
{
    public static IEndpointRouteBuilder MapPricesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/prices").WithTags("Prices");

        group.MapPost("/refresh", async Task<Results<Ok<PriceRefreshCycleResult>, ProblemHttpResult>> (
            IPriceRefreshService refreshService,
            CancellationToken cancellationToken) =>
        {
            var result = await refreshService.RefreshNowAsync(cancellationToken);

            if (result.Outcome == PriceRefreshOutcome.CooldownActive)
            {
                return TypedResults.Problem(
                    title: "Refresh cooldown active",
                    detail: $"A manual refresh was triggered too recently. Try again in {result.CooldownSecondsRemaining} second(s).",
                    statusCode: StatusCodes.Status429TooManyRequests,
                    extensions: new Dictionary<string, object?> { ["secondsRemaining"] = result.CooldownSecondsRemaining });
            }

            return TypedResults.Ok(result);
        });

        group.MapGet("/status", async (
            PriceRefreshStatusStore statusStore,
            IMarketCalendar calendar,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow();
            var status = await statusStore.GetSnapshotAsync(
                calendar.IsOpen(Market.Nyse, now),
                calendar.IsOpen(Market.Sgx, now),
                cancellationToken);

            return TypedResults.Ok(status);
        });

        return app;
    }
}
