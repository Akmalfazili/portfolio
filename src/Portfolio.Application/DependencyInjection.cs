using Microsoft.Extensions.DependencyInjection;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services;
using Portfolio.Application.Services.Calculators;
using Portfolio.Application.Services.Calendar;

namespace Portfolio.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAssetService, AssetService>();
        services.AddScoped<ITransactionService, TransactionService>();
        services.AddScoped<IPriceBackfillService, PriceBackfillService>();
        services.AddHostedService<PriceBackfillBackgroundService>();
        services.AddScoped<IDividendBackfillService, DividendBackfillService>();
        services.AddHostedService<DividendBackfillBackgroundService>();

        // Pure, stateless calculators — no DB dependency, so singletons rather than scoped.
        services.AddSingleton<ICostBasisCalculator, AverageCostCalculator>();
        services.AddSingleton<IPerformanceSeriesBuilder, PerformanceSeriesBuilder>();
        services.AddSingleton<IAnnualReturnCalculator, AnnualReturnCalculator>();
        services.AddSingleton<IDividendIncomeCalculator, DividendIncomeCalculator>();

        services.AddScoped<IPortfolioSummaryService, PortfolioSummaryService>();
        services.AddScoped<IPortfolioPerformanceService, PortfolioPerformanceService>();
        services.AddScoped<IDividendService, DividendService>();

        services.AddSingleton<IMarketCalendar, MarketCalendar>();

        // Singleton: the whole point of the throttle is a rolling window and a manual-refresh
        // in-flight flag shared across every caller for the process lifetime — a scoped instance
        // would remember nothing between requests. It reaches the scoped IPortfolioDbContext for
        // the persisted daily ledger via IServiceScopeFactory instead.
        services.AddSingleton<ITwelveDataCreditThrottle, TwelveDataCreditThrottle>();
        services.AddSingleton<ManualRefreshInFlightGate>();
        services.AddSingleton<ManualBackfillInFlightGate>();
        services.AddSingleton<ManualDividendBackfillInFlightGate>();

        // Scoped, not singleton: the status store now reads and writes SourceRefreshState through
        // the scoped IPortfolioDbContext, so it must share the ambient scope's DbContext rather
        // than capturing one for the process lifetime.
        services.AddScoped<PriceRefreshStatusStore>();
        // Registered under its own concrete type too, not just the interface: a manual refresh
        // that needs to detach a large Twelve Data sweep (D38) resolves a second instance of this
        // same type from a fresh scope via IServiceScopeFactory, so it needs its own db/router/etc
        // rather than the disposed-by-then request scope's.
        services.AddScoped<PriceRefreshService>();
        services.AddScoped<IPriceRefreshService>(sp => sp.GetRequiredService<PriceRefreshService>());
        services.AddHostedService<PriceRefreshBackgroundService>();

        return services;
    }
}
