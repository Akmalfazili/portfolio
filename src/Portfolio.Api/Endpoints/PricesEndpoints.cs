using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Application.Services.Calendar;
using Portfolio.Domain.Enums;

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

        // D12: manual trigger for backfilling PriceHistory/FxRate — bounded by the derived-from-
        // remaining-credits budget the scheduled daily run also respects (D37), and safe to call
        // any time, including while the market is open, since it never touches PriceQuote and is
        // idempotent against the (AssetId, Date) unique index.
        //
        // D37/D38 follow-up, found live: once every Twelve Data call is paced through the shared
        // credit throttle, a full pass can take minutes — long enough that nginx's default proxy
        // read timeout 504s the request while the API keeps running it, and the aborted
        // connection's CancellationToken then cancels every remaining call mid-run, each one
        // misreported as a provider failure in the audit trail. Detached exactly like the manual
        // quote refresh (see PriceRefreshService.RefreshNowAsync) so it is never bound to this
        // request's lifetime — poll GET /api/prices/status or the RefreshRun audit trail for the
        // outcome instead of reading it from this response.
        group.MapPost("/backfill", Results<Accepted<BackfillQueuedResult>, ProblemHttpResult> (
            IServiceScopeFactory scopeFactory,
            ManualBackfillInFlightGate inFlightGate,
            ILogger<Program> logger) =>
        {
            if (!inFlightGate.TryEnter())
            {
                return TypedResults.Problem(
                    title: "Backfill already in progress",
                    detail: "A manually triggered backfill is already running in the background. Try again once it completes.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var backfillService = scope.ServiceProvider.GetRequiredService<IPriceBackfillService>();
                    // Manual trigger bypasses due-ness entirely and always covers every market
                    // (D47) — unlike the scheduled path, this is a deliberate on-demand request,
                    // not something the market calendar should be allowed to defer.
                    await backfillService.RunAsync(RefreshTrigger.BackfillManual, ProviderMarkets.All, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Detached manual price backfill failed");
                }
                finally
                {
                    inFlightGate.Exit();
                }
            }, CancellationToken.None);

            return TypedResults.Accepted((string?)null, new BackfillQueuedResult(Queued: true));
        });

        group.MapGet("/status", async (
            PriceRefreshStatusStore statusStore,
            IMarketCalendar calendar,
            ITwelveDataCreditThrottle creditThrottle,
            IPortfolioDbContext db,
            IOptions<PriceRefreshOptions> refreshOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow();
            var status = await statusStore.GetSnapshotAsync(
                calendar.IsOpen(Market.Nyse, now),
                calendar.IsOpen(Market.Sgx, now),
                cancellationToken);

            // D38/D37: surface the derived cadence and today's credit spend so degradation as the
            // portfolio grows is visible on the wire rather than silently inferred — additive
            // fields only, the pre-existing shape is untouched.
            var creditStatus = await creditThrottle.GetStatusAsync(cancellationToken);
            var activeTwelveDataCount = await db.Assets.CountAsync(
                a => a.IsActive && a.QuoteProviderKind == QuoteProviderKind.TwelveData, cancellationToken);
            var effectiveInterval = TwelveDataCadenceCalculator.DeriveStockOpenInterval(
                activeTwelveDataCount,
                creditStatus.RemainingToday,
                TimeSpan.FromMinutes(TwelveDataCreditPolicy.NyseSessionMinutes),
                refreshOptions.Value.StockOpenInterval);

            var extended = status with
            {
                EffectiveTwelveDataIntervalSeconds = (int)effectiveInterval.TotalSeconds,
                CreditsUsedToday = creditStatus.CreditsUsedToday,
                CreditBudget = creditStatus.DailyBudget,
            };

            return TypedResults.Ok(extended);
        });

        return app;
    }
}
