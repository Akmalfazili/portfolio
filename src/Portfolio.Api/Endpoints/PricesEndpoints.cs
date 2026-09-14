using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            IRefreshCatchUpService catchUpService,
            IServiceScopeFactory scopeFactory,
            ManualBackfillInFlightGate backfillGate,
            ManualDividendBackfillInFlightGate dividendGate,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var result = await refreshService.RefreshNowAsync(cancellationToken);

            // Refresh-catch-up feature: ShouldRunCatchUp is false only for CooldownActive — the
            // quote refresh above was NOT accepted, from either branch RefreshNowAsync can return
            // that outcome from (the ordinary cooldown check, or the in-flight-gate reuse when a
            // previous click's detached Twelve Data sweep is still running; both surface as the same
            // enum value here). One check drives both the 429 below and whether catch-up runs at
            // all, so the two can never drift apart — see tracker.md's refresh-catch-up entry and
            // ShouldRunCatchUp, pinned directly by RefreshCatchUpWiringTests.
            if (!ShouldRunCatchUp(result.Outcome))
            {
                return TypedResults.Problem(
                    title: "Refresh cooldown active",
                    detail: $"A manual refresh was triggered too recently. Try again in {result.CooldownSecondsRemaining} second(s).",
                    statusCode: StatusCodes.Status429TooManyRequests,
                    extensions: new Dictionary<string, object?> { ["secondsRemaining"] = result.CooldownSecondsRemaining });
            }

            // PlanAsync is DB-only (no provider call), so it is safe to run inline here; only the
            // two backfills it may then queue are detached, exactly like /api/prices/backfill and
            // /api/dividends/backfill (D37/D38) — this handler must still return promptly.
            var candidates = await catchUpService.PlanAsync(cancellationToken);
            var priceHistoryLeg = StartPriceHistoryCatchUp(candidates.PriceHistory, scopeFactory, backfillGate, logger);
            var dividendsLeg = StartDividendsCatchUp(candidates.Dividends, scopeFactory, dividendGate, logger);

            result = result with { CatchUp = new RefreshCatchUpPlan(priceHistoryLeg, dividendsLeg) };
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
            PriceRefreshStatusEnricher enricher,
            IMarketCalendar calendar,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow();
            var status = await statusStore.GetSnapshotAsync(
                calendar.IsOpen(Market.Nyse, now),
                calendar.IsOpen(Market.Sgx, now),
                cancellationToken);

            // D38/D37: surface the derived cadence and today's credit spend so degradation as the
            // portfolio grows is visible on the wire rather than silently inferred. The enrichment
            // itself now lives in one place (PriceRefreshStatusEnricher) shared with the SignalR
            // hub-connect and broadcast paths, so this endpoint's payload is identical to what those
            // two now also send instead of being the only producer that ever populated these fields.
            var extended = await enricher.EnrichAsync(status, cancellationToken);

            return TypedResults.Ok(extended);
        });

        return app;
    }

    /// <summary>
    /// Refresh-catch-up feature: whether the quote refresh outcome from <c>RefreshNowAsync</c>
    /// counts as "accepted" for the catch-up planner to even run. False only for
    /// <see cref="PriceRefreshOutcome.CooldownActive"/> — every other outcome (including
    /// <see cref="PriceRefreshOutcome.NothingDue"/>, a manual click landing while every quote source
    /// happens to be gated) still lets catch-up run, since price-history/dividend coverage is an
    /// independent question from whether a live quote was fetched this click. Internal (not
    /// private) and pulled out of the handler lambda specifically so
    /// <c>RefreshCatchUpWiringTests</c> can pin it without a full HTTP round trip.
    /// </summary>
    internal static bool ShouldRunCatchUp(PriceRefreshOutcome outcome) => outcome != PriceRefreshOutcome.CooldownActive;

    /// <summary>Price-history leg: something is missing when there is a symbol to fetch OR an FX
    /// pair to fetch — an FX-only catch-up (an asset's own history already covered, but its
    /// currency's stored rate is stale — see <c>IRefreshCatchUpService</c>'s FX remarks) still
    /// counts as work to do.</summary>
    internal static bool HasPriceHistoryWorkToDo(PriceHistoryCatchUpCandidates candidates) =>
        candidates.ToFetch.Count > 0 || candidates.CurrenciesToFetch.Count > 0;

    internal static bool HasDividendsWorkToDo(DividendsCatchUpCandidates candidates) =>
        candidates.ToFetch.Count > 0;

    /// <summary>Shapes a price-history <see cref="CatchUpLeg"/> for a given <paramref name="state"/> —
    /// pure, no side effects, so <c>RefreshCatchUpWiringTests</c> can assert the wire shape for
    /// <see cref="CatchUpState.NothingToFetch"/>/<see cref="CatchUpState.AlreadyRunning"/> without
    /// resolving anything from DI. <paramref name="runId"/> must be non-null if and only if
    /// <paramref name="state"/> is <see cref="CatchUpState.Queued"/> — see
    /// <see cref="CatchUpLeg.RunId"/>'s own remarks; left null for the other two states, which start
    /// nothing to correlate a completion push with.</summary>
    internal static CatchUpLeg BuildPriceHistoryLeg(PriceHistoryCatchUpCandidates candidates, CatchUpState state, Guid? runId = null) =>
        new(
            state,
            candidates.ToFetch.Select(t => t.Symbol).ToList(),
            candidates.CurrenciesToFetch.Select(c => $"USD/{c}").ToList(),
            candidates.RetryPendingSymbols,
            candidates.NotYetAvailableSymbols,
            runId);

    /// <summary>Dividends leg equivalent of <see cref="BuildPriceHistoryLeg"/> — always empty
    /// <c>FxPairs</c>/<c>NotYetAvailableSymbols</c>, per the wire contract. Same
    /// <paramref name="runId"/> non-null-iff-Queued rule.</summary>
    internal static CatchUpLeg BuildDividendsLeg(DividendsCatchUpCandidates candidates, CatchUpState state, Guid? runId = null) =>
        new(state, candidates.ToFetch.Select(t => t.Symbol).ToList(), [], candidates.RetryPendingSymbols, [], runId);

    /// <summary>
    /// Refresh-catch-up feature, price-history leg. Never awaits the fetch itself — mirrors
    /// <c>POST /api/prices/backfill</c>'s own detaching exactly, and shares its in-flight gate
    /// (<see cref="ManualBackfillInFlightGate"/>) so a catch-up can never overlap a manual full
    /// backfill of the same kind. Internal (not private), like the decision helpers above, so
    /// <c>RefreshCatchUpWiringTests</c> can pin "nothing missing takes no gate" and "a held gate
    /// reports AlreadyRunning and starts nothing" against the real gate object, not just the pure
    /// decision it delegates to.
    /// </summary>
    internal static CatchUpLeg StartPriceHistoryCatchUp(
        PriceHistoryCatchUpCandidates candidates,
        IServiceScopeFactory scopeFactory,
        ManualBackfillInFlightGate gate,
        ILogger<Program> logger)
    {
        if (!HasPriceHistoryWorkToDo(candidates))
        {
            return BuildPriceHistoryLeg(candidates, CatchUpState.NothingToFetch);
        }

        if (!gate.TryEnter())
        {
            return BuildPriceHistoryLeg(candidates, CatchUpState.AlreadyRunning);
        }

        var assetIds = candidates.ToFetch.Select(t => t.AssetId).ToHashSet();
        var currencies = candidates.CurrenciesToFetch.ToHashSet();
        var markets = candidates.MarketsInPlay;

        // Minted BEFORE the detached task starts, not inside it: the task can (and, for a fast
        // dividends leg, routinely does) finish and broadcast CatchUpCompleted before this method
        // even returns to the caller, let alone before the browser has processed the HTTP response
        // — so the run id must exist in time to be embedded in the Queued leg returned below, not
        // generated only once the task itself gets scheduled.
        var runId = Guid.NewGuid();

        // The whole body — scope creation, every GetRequiredService call, the run itself, and every
        // broadcast — sits inside this outermost try/finally, so gate.Exit() is guaranteed on every
        // path (coordinator review fix 2a): before this fix, scope creation and the broadcaster/
        // timeProvider resolution sat OUTSIDE the try/finally, so a throw there — vanishingly
        // unlikely but not impossible (e.g. the DI container torn down mid-shutdown) — left the gate
        // (shared with the manual POST /api/prices/backfill endpoint) held until process restart.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var broadcaster = scope.ServiceProvider.GetRequiredService<IPriceUpdateBroadcaster>();
                var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();

                try
                {
                    var backfillService = scope.ServiceProvider.GetRequiredService<IPriceBackfillService>();
                    var summary = await backfillService.RunCatchUpAsync(markets, assetIds, currencies, CancellationToken.None);

                    // Broadcast BEFORE gate.Exit() in the outermost finally below (both here and on
                    // the catch path) — deliberately, so a new click cannot claim the gate and queue
                    // a second run of this kind while this run's completion is still unsent.
                    await SafeBroadcastCatchUpCompletedAsync(
                        broadcaster,
                        new CatchUpCompletedNotification(
                            CatchUpKind.PriceHistory,
                            summary.AssetsProcessed,
                            summary.AssetsFailed.Select(f => new CatchUpFailure(f.Symbol, f.Error)).ToList(),
                            summary.AssetsSkippedForBudget,
                            summary.PriceHistoryPointsInserted + summary.FxRatePointsInserted,
                            timeProvider.GetUtcNow(),
                            runId),
                        logger);

                    // Coordinator review fix 2b: its OWN try/catch, log-only — this used to share
                    // the outer try with the success broadcast above, so a throw here (status
                    // snapshot/enrich/broadcast) fell into the catch below and sent a SECOND
                    // CatchUpCompleted for the same runId reporting a failure for a run that had
                    // already succeeded. Unlike the dividends leg, this also re-broadcasts a freshly
                    // enriched RefreshStatus — the refresh panel's closes[] (per-market
                    // LatestCloseDate) only ever updates from a RefreshStatus push, and a catch-up
                    // landing a new close must not wait for the next scheduled/manual quote cycle to
                    // surface it — but that is a best-effort nicety, not part of "did the run
                    // succeed", so its own failure must never be reported as the run's.
                    try
                    {
                        var statusStore = scope.ServiceProvider.GetRequiredService<PriceRefreshStatusStore>();
                        var statusEnricher = scope.ServiceProvider.GetRequiredService<PriceRefreshStatusEnricher>();
                        var calendar = scope.ServiceProvider.GetRequiredService<IMarketCalendar>();
                        var now = timeProvider.GetUtcNow();
                        var status = await statusStore.GetSnapshotAsync(
                            calendar.IsOpen(Market.Nyse, now), calendar.IsOpen(Market.Sgx, now), CancellationToken.None);
                        var extended = await statusEnricher.EnrichAsync(status, CancellationToken.None);
                        await broadcaster.BroadcastRefreshStatusAsync(extended, CancellationToken.None);
                    }
                    catch (Exception statusEx)
                    {
                        logger.LogError(statusEx, "Failed to re-broadcast RefreshStatus after price-history catch-up {RunId}", runId);
                    }
                }
                catch (Exception ex)
                {
                    // The run itself threw outside the per-asset try/catch RunAsyncCore already
                    // wraps every provider call in (e.g. a DB failure) — still broadcast
                    // CatchUpCompleted so the panel never waits forever on a leg that died. An empty
                    // symbol (not the asset/pair itself, since the whole run died, not one of them)
                    // reads as "Couldn't fetch price history — <error>" on the frontend.
                    logger.LogError(ex, "Detached price-history catch-up failed");
                    await SafeBroadcastCatchUpCompletedAsync(
                        broadcaster,
                        new CatchUpCompletedNotification(
                            CatchUpKind.PriceHistory, [], [new CatchUpFailure("", ex.Message)], [], 0, timeProvider.GetUtcNow(), runId),
                        logger);
                }
            }
            catch (Exception ex)
            {
                // Scope creation or resolving IPriceUpdateBroadcaster/TimeProvider itself failed —
                // nothing exists to broadcast through. Log only; the gate is still released below.
                logger.LogError(ex, "Detached price-history catch-up failed before its scope could be set up");
            }
            finally
            {
                gate.Exit();
            }
        }, CancellationToken.None);

        return BuildPriceHistoryLeg(candidates, CatchUpState.Queued, runId);
    }

    /// <summary>Coordinator review fix 2c: the broadcast on the failure path can itself throw (e.g.
    /// SignalR transport down) — guarded here (log only) so a detached catch-up task never ends
    /// with an unobserved exception. Shared by both legs' success AND failure broadcasts, not just
    /// the failure path, for the same reason.</summary>
    private static async Task SafeBroadcastCatchUpCompletedAsync(
        IPriceUpdateBroadcaster broadcaster, CatchUpCompletedNotification notification, ILogger<Program> logger)
    {
        try
        {
            await broadcaster.BroadcastCatchUpCompletedAsync(notification, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to broadcast CatchUpCompleted for {Kind} catch-up run {RunId}",
                notification.Kind,
                notification.RunId);
        }
    }

    /// <summary>
    /// Refresh-catch-up feature, dividends leg. Mirrors <see cref="StartPriceHistoryCatchUp"/>
    /// exactly, using <see cref="ManualDividendBackfillInFlightGate"/> — deliberately independent of
    /// the price-history leg's gate, so the two catch-up legs (and a concurrent manual full backfill
    /// of either kind) can run without tripping each other. Never re-broadcasts RefreshStatus — that
    /// panel has no dividend-specific field driven off a fresh push the way price history's
    /// closes[] is. Internal for the same reason as <see cref="StartPriceHistoryCatchUp"/>.
    /// </summary>
    internal static CatchUpLeg StartDividendsCatchUp(
        DividendsCatchUpCandidates candidates,
        IServiceScopeFactory scopeFactory,
        ManualDividendBackfillInFlightGate gate,
        ILogger<Program> logger)
    {
        if (!HasDividendsWorkToDo(candidates))
        {
            return BuildDividendsLeg(candidates, CatchUpState.NothingToFetch);
        }

        if (!gate.TryEnter())
        {
            return BuildDividendsLeg(candidates, CatchUpState.AlreadyRunning);
        }

        var assetIds = candidates.ToFetch.Select(t => t.AssetId).ToHashSet();

        // See StartPriceHistoryCatchUp's own remarks: minted before the task starts, since a
        // single-asset dividends leg (one free Yahoo call, ~300ms) routinely completes and
        // broadcasts before this method has even returned to its caller.
        var runId = Guid.NewGuid();

        // See StartPriceHistoryCatchUp's identical remark (coordinator review fix 2a): the whole
        // body sits inside this outermost try/finally so gate.Exit() is guaranteed on every path,
        // including scope creation/service resolution itself failing.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var broadcaster = scope.ServiceProvider.GetRequiredService<IPriceUpdateBroadcaster>();
                var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();

                try
                {
                    var backfillService = scope.ServiceProvider.GetRequiredService<IDividendBackfillService>();
                    var summary = await backfillService.RunCatchUpAsync(assetIds, CancellationToken.None);

                    // Broadcast BEFORE gate.Exit() in the outermost finally below — see
                    // StartPriceHistoryCatchUp's identical remark.
                    await SafeBroadcastCatchUpCompletedAsync(
                        broadcaster,
                        new CatchUpCompletedNotification(
                            CatchUpKind.Dividends,
                            summary.AssetsProcessed,
                            summary.AssetsFailed.Select(f => new CatchUpFailure(f.Symbol, f.Error)).ToList(),
                            summary.AssetsSkippedForBudget,
                            summary.DividendEventsInserted,
                            timeProvider.GetUtcNow(),
                            runId),
                        logger);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Detached dividend catch-up failed");
                    await SafeBroadcastCatchUpCompletedAsync(
                        broadcaster,
                        new CatchUpCompletedNotification(
                            CatchUpKind.Dividends, [], [new CatchUpFailure("", ex.Message)], [], 0, timeProvider.GetUtcNow(), runId),
                        logger);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Detached dividend catch-up failed before its scope could be set up");
            }
            finally
            {
                gate.Exit();
            }
        }, CancellationToken.None);

        return BuildDividendsLeg(candidates, CatchUpState.Queued, runId);
    }
}
