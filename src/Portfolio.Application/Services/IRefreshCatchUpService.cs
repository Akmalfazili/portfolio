using Portfolio.Domain.Enums;

namespace Portfolio.Application.Services;

/// <summary>One stock asset a catch-up leg wants to fetch — carries the id
/// <c>IPriceBackfillService.RunCatchUpAsync</c>/<c>IDividendBackfillService.RunCatchUpAsync</c> need
/// alongside the symbol the wire-contract <c>Dtos.CatchUpLeg</c> reports, and the asset's own market
/// (used by the price-history leg to derive <c>MarketsInPlay</c> — unused, but carried uniformly,
/// by the dividends leg, which has no market scoping of its own).</summary>
public sealed record CatchUpAssetTarget(int AssetId, string Symbol, Market Market);

/// <summary>
/// <see cref="IRefreshCatchUpService"/>'s price-history findings — the DB-only candidates the
/// endpoint turns into a <c>Dtos.CatchUpLeg</c> and, if non-empty, an
/// <c>IPriceBackfillService.RunCatchUpAsync</c> call. See <see cref="IRefreshCatchUpService"/> for
/// exactly how each list is decided.
/// </summary>
public sealed record PriceHistoryCatchUpCandidates(
    IReadOnlyList<CatchUpAssetTarget> ToFetch,
    /// <summary>Currency codes (e.g. <c>"SGD"</c>), not <c>"USD/xxx"</c> pair strings — the shape
    /// <c>IPriceBackfillService.RunCatchUpAsync</c>'s <c>currencies</c> parameter and
    /// <c>PriceBackfillService.RunAsyncCore</c>'s own <c>currenciesNeedingFx</c> already use. The
    /// endpoint prepends <c>"USD/"</c> only when building the wire-contract <c>Dtos.CatchUpLeg.FxPairs</c>.</summary>
    IReadOnlyList<string> CurrenciesToFetch,
    IReadOnlyList<string> RetryPendingSymbols,
    IReadOnlyList<string> NotYetAvailableSymbols,
    /// <summary>Every market with an asset in <see cref="ToFetch"/>, or requiring a currency in
    /// <see cref="CurrenciesToFetch"/> — exactly the <c>markets</c> argument
    /// <c>IPriceBackfillService.RunCatchUpAsync</c> needs so its per-market
    /// <see cref="Domain.Entities.RefreshRun"/> rows land only on markets this run actually
    /// touches (an FX-only catch-up, with no asset being fetched, still needs its requiring
    /// market(s) here for that reason).</summary>
    IReadOnlyList<Market> MarketsInPlay);

/// <summary>
/// <see cref="IRefreshCatchUpService"/>'s dividend findings — dividends have no market scoping and
/// no FX leg of their own (<c>DividendBackfillService.RunAsyncCore</c> writes one un-market-scoped
/// <see cref="Domain.Entities.RefreshRun"/> row, unlike the per-market price-history rows), so this
/// carries none of <see cref="PriceHistoryCatchUpCandidates"/>'s extra fields.
/// </summary>
public sealed record DividendsCatchUpCandidates(
    IReadOnlyList<CatchUpAssetTarget> ToFetch,
    IReadOnlyList<string> RetryPendingSymbols);

/// <summary>Both legs' candidates from one <see cref="IRefreshCatchUpService.PlanAsync"/> call.</summary>
public sealed record RefreshCatchUpCandidates(
    PriceHistoryCatchUpCandidates PriceHistory, DividendsCatchUpCandidates Dividends);

/// <summary>
/// The refresh-catch-up feature's planner: decides, from stored data alone (no provider calls —
/// every query here is a cheap local SQL Server read), exactly which in-scope stock assets are
/// missing <see cref="Domain.Entities.PriceHistory"/>/<see cref="Domain.Entities.FxRate"/> coverage
/// or <see cref="Domain.Entities.DividendEvent"/> coverage, so <c>POST /api/prices/refresh</c> can
/// fetch only that — zero extra provider calls on an ordinary click, ~1 credit + 1 free Yahoo call
/// right after a new stock is added. See tracker.md's refresh-catch-up entry for the full design
/// and the measured permanent-gap examples (AAPL, AMZN, NIO, NVDA, PG, ARVLF, Z74) this exists to
/// never re-fetch on every click.
///
/// <para><b>Scope: active <see cref="Domain.Enums.AssetClass.Stock"/> assets with at least one
/// transaction. Crypto never appears</b> — it keeps no price history and pays no dividends, the same
/// locked decision <c>PriceBackfillService</c>/<c>DividendBackfillService</c> already enforce.</para>
///
/// <para><b>Price history, per asset.</b> <c>cap</c> is this asset's own market's settled cap (see
/// <see cref="Calculators.PriceBackfillCapCalculator"/> — the exact computation
/// <c>PriceBackfillService.RunAsyncCore</c> uses, extracted to one place so the two can never drift
/// apart). <c>firstTrade</c> is the asset's earliest trade date. If <c>firstTrade &gt; cap</c>, no
/// settled close exists yet — <see cref="PriceHistoryCatchUpCandidates.NotYetAvailableSymbols"/>,
/// not attempted and not missing. Otherwise the asset is COVERED — and excluded from
/// <see cref="PriceHistoryCatchUpCandidates.ToFetch"/> entirely — when EITHER stored
/// <see cref="Domain.Entities.PriceHistory"/> spans <c>[firstTrade, cap]</c>, OR
/// <see cref="Domain.Entities.AssetPriceHistoryState"/> says the provider was already successfully
/// asked for that range. The stored-data leg is what makes this cheap from day one, before any
/// state rows exist; the state leg is what stops a permanently-gapped asset (a weekend/holiday
/// first trade, or a SPAC listed after it — see the class remarks' examples) from reading as
/// missing forever. Otherwise the asset is missing — but if its state row's last attempt failed
/// recently (<see cref="PriceBackfillOptions.FailedRunRetryDelay"/>), it is
/// <see cref="PriceHistoryCatchUpCandidates.RetryPendingSymbols"/> instead: missing, but not
/// attempted this click.</para>
///
/// <para><b>FX (price-history leg only).</b> For each non-USD currency an in-scope asset needs,
/// <c>required = min(the latest settled cap among the markets that need it, fxCap)</c> — see
/// <see cref="Calculators.PriceBackfillCapCalculator.ComputeFxCap"/>; comparing against a market's
/// cap alone, without also capping at <c>fxCap</c>, is wrong: <c>RunAsyncCore</c> never requests or
/// accepts a rate beyond <c>fxCap</c>, so after a market settles but before <c>fxCap</c> itself
/// advances, FX would read as missing (and spend a credit, and insert nothing) on every click for
/// hours. The pair is fetched when EITHER (a) an asset of that currency is itself in
/// <see cref="PriceHistoryCatchUpCandidates.ToFetch"/> this run — a hard requirement, never deferred
/// by retry pacing, since <c>FxRateResolver</c> throws without a rate for a date being converted —
/// OR (b), when (a) does not apply, the pair is not covered: neither stored
/// <see cref="Domain.Entities.FxRate"/> nor <see cref="Domain.Entities.FxPairBackfillState.CoveredTo"/>
/// reaches <c>required</c>. <see cref="Domain.Entities.FxPairBackfillState"/> exists because
/// <c>required</c> alone is still not sufficient: Twelve Data does not reliably publish a bar for
/// every calendar day either (measured live: 54 Fridays but only 35 Sundays with a stored USD/SGD
/// row since 2025-09-01), so a stored-data-only comparison against a Sunday <c>required</c> date can
/// read as missing forever — the same permanent-gap trap the asset leg's own state table closes,
/// applied here to FX. Case (b) only, and only when not already covered, is paced by a recent
/// failure the same way the asset leg is (<see cref="PriceBackfillOptions.FailedRunRetryDelay"/>),
/// reported as <c>"USD/{currency}"</c> in <see cref="PriceHistoryCatchUpCandidates.RetryPendingSymbols"/>
/// (the same list asset symbols use, not a separate one). Deliberately NO lower-bound check on its
/// own: FX carry-forward already covers a weekend-dated first trade, and checking the lower bound
/// alone would reopen the permanent-gap-every-click trap this whole feature exists to close.</para>
///
/// <para><b>Dividends, per asset.</b> Missing when there is no <see cref="Domain.Entities.AssetDividendState"/>
/// row, OR its last run failed, OR <see cref="Domain.Entities.AssetDividendState.CoveredFrom"/> is
/// null (every pre-migration row — the first catch-up click after deploy re-fetches every stock's
/// dividends from Yahoo once, free, after which this stops tripping), OR <c>CoveredFrom &gt;
/// firstTrade</c> (a back-dated transaction moved the asset's earliest trade date earlier than what
/// was last asked for). RetryPending under the same "failed recently" rule, paced by
/// <see cref="DividendBackfillOptions.FailedAssetRetryInterval"/> instead.</para>
///
/// <para><b>Expected consequence of the state leg starting empty</b> (stated here, not just in
/// tracker.md): before the first scheduled backfill run after this feature deploys writes any
/// <see cref="Domain.Entities.AssetPriceHistoryState"/> rows, the first catch-up click will fetch
/// every asset with a permanent price-history gap (the AAPL/AMZN/NIO/NVDA/PG/ARVLF/Z74 shape) once —
/// a one-time cost, not a per-click one, since that attempt then writes the state row that covers
/// them from then on.</para>
/// </summary>
public interface IRefreshCatchUpService
{
    /// <summary>DB-only — never calls a market-data provider. Safe to call on every manual refresh
    /// click, including one that finds nothing missing.</summary>
    Task<RefreshCatchUpCandidates> PlanAsync(CancellationToken cancellationToken);
}
