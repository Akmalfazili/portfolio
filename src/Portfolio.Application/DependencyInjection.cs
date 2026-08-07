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

        // Pure, stateless calculators — no DB dependency, so singletons rather than scoped.
        services.AddSingleton<ICostBasisCalculator, AverageCostCalculator>();
        services.AddSingleton<IPerformanceSeriesBuilder, PerformanceSeriesBuilder>();
        services.AddSingleton<IAnnualReturnCalculator, AnnualReturnCalculator>();

        services.AddScoped<IPortfolioSummaryService, PortfolioSummaryService>();
        services.AddScoped<IPortfolioPerformanceService, PortfolioPerformanceService>();

        services.AddSingleton<IMarketCalendar, MarketCalendar>();

        // Scoped, not singleton: the status store now reads and writes SourceRefreshState through
        // the scoped IPortfolioDbContext, so it must share the ambient scope's DbContext rather
        // than capturing one for the process lifetime.
        services.AddScoped<PriceRefreshStatusStore>();
        services.AddScoped<IPriceRefreshService, PriceRefreshService>();
        services.AddHostedService<PriceRefreshBackgroundService>();

        return services;
    }
}
