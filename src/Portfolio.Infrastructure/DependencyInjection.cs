using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Portfolio.Application.Abstractions;
using Portfolio.Application.Services;
using Portfolio.Infrastructure.MarketData;
using Portfolio.Infrastructure.MarketData.CoinGecko;
using Portfolio.Infrastructure.MarketData.TwelveData;
using Portfolio.Infrastructure.MarketData.Yahoo;
using Portfolio.Infrastructure.Persistence;

namespace Portfolio.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers <see cref="PortfolioDbContext"/> against the "Portfolio" connection string.
    /// The same registration works for both environments: locally it resolves the Windows-auth
    /// SQLEXPRESS string from <c>appsettings.Development.json</c>; in the Docker container it
    /// resolves the SQL-auth string supplied via the <c>ConnectionStrings__Portfolio</c>
    /// environment variable, which the standard configuration provider chain overrides
    /// appsettings.json with automatically. Never hardcode a connection string here.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Portfolio")
            ?? throw new InvalidOperationException(
                "Connection string 'Portfolio' is not configured. Set it in appsettings.Development.json " +
                "for local runs or via the ConnectionStrings__Portfolio environment variable in containers.");

        services.AddDbContext<PortfolioDbContext>(options => options.UseSqlServer(
            connectionString,
            sql => sql.MigrationsAssembly(typeof(PortfolioDbContext).Assembly.FullName)));

        services.AddScoped<IPortfolioDbContext>(sp => sp.GetRequiredService<PortfolioDbContext>());

        services.AddMarketData(configuration);

        return services;
    }

    /// <summary>
    /// Wires up the three market-data providers (Twelve Data for US equities + FX, CoinGecko for
    /// crypto, Yahoo Finance for SGX/Z74), the router that dispatches an asset to the right one,
    /// and the historical backfill service. Every typed <see cref="HttpClient"/> here has its
    /// default logging handlers removed and replaced with <see cref="RedactingLoggingHandler"/>
    /// so a failed request — which is exactly when the framework's default handlers would log
    /// the full request URI, apikey and all — can never print a live key.
    /// </summary>
    private static IServiceCollection AddMarketData(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TwelveDataOptions>(configuration.GetSection(TwelveDataOptions.SectionName));
        // Same "TwelveData" config node as above — TwelveDataOptions binds ApiKey/BaseUrl,
        // TwelveDataCreditOptions binds the credit-limit numbers. Both bind independently; adding
        // this never disturbs the existing secret-handling for ApiKey.
        services.Configure<TwelveDataCreditOptions>(configuration.GetSection(TwelveDataCreditOptions.SectionName));
        services.Configure<CoinGeckoOptions>(configuration.GetSection(CoinGeckoOptions.SectionName));
        services.Configure<YahooOptions>(configuration.GetSection(YahooOptions.SectionName));
        // IPriceBackfillService/PriceBackfillService are Application-layer types registered by
        // AddApplication(); options binding lives here because Portfolio.Application deliberately
        // has no IConfiguration package reference (kept persistence/config-ignorant, matching the
        // Phase 3 decision to keep it thin) while Portfolio.Infrastructure already does.
        services.Configure<PriceBackfillOptions>(configuration.GetSection(PriceBackfillOptions.SectionName));
        services.Configure<PriceRefreshOptions>(configuration.GetSection(PriceRefreshOptions.SectionName));

        services.AddTransient<RedactingLoggingHandler>();

        // Twelve Data: 800 credits/day, 8 req/min free tier. A batch of N symbols costs N
        // credits, so the resilience policy must not amplify a single failure into several
        // retries — no retry at all on 429 (that would burn more of the daily budget for
        // nothing), a couple of short retries only for transient 5xx/network failures, and a
        // circuit breaker so a sustained outage stops being hammered altogether.
        services.AddHttpClient<TwelveDataQuoteProvider>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<TwelveDataOptions>>().Value;
                client.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
            })
            .RemoveAllLoggers()
            .AddHttpMessageHandler<RedactingLoggingHandler>()
            .AddResilienceHandler("twelve-data", ConfigureTwelveDataResilience);

        services.AddHttpClient<TwelveDataFxProvider>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<TwelveDataOptions>>().Value;
                client.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
            })
            .RemoveAllLoggers()
            .AddHttpMessageHandler<RedactingLoggingHandler>()
            .AddResilienceHandler("twelve-data-fx", ConfigureTwelveDataResilience);

        // CoinGecko: keyless public API, no query-string secret to leak, but the same
        // redacting/no-default-logging pipeline is applied for consistency and in case a Demo/
        // Pro key is configured later (sent as a header, not logged by the custom handler).
        // Verified live during this build: CoinGecko sits behind Cloudflare, which 403s a
        // .NET HttpClient's default (empty) User-Agent even though the identical request works
        // fine from curl — a User-Agent header is required in practice, not just for Yahoo.
        services.AddHttpClient<CoinGeckoQuoteProvider>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<CoinGeckoOptions>>().Value;
                // D32: route on HasApiKey, never a bare "ApiKey is null" check — a compose .env
                // that names CoinGecko__ApiKey with a blank value binds it to "", which is
                // present-but-empty, not absent, and `is null` would wrongly route to the paid
                // Pro API with no key attached, which 401s.
                var baseUrl = options.HasApiKey ? options.ProBaseUrl : options.KeylessBaseUrl;
                client.BaseAddress = new Uri(EnsureTrailingSlash(baseUrl));
                client.DefaultRequestHeaders.UserAgent.ParseAdd("PortfolioTracker/1.0 (+https://github.com/)");
                if (options.HasApiKey)
                {
                    client.DefaultRequestHeaders.Add("x-cg-pro-api-key", options.ApiKey);
                }
            })
            .RemoveAllLoggers()
            .AddHttpMessageHandler<RedactingLoggingHandler>()
            .AddResilienceHandler("coingecko", ConfigureStandardResilience);

        // Yahoo: unofficial, undocumented, no SLA — treated as fallible by the provider itself,
        // so the resilience policy here just needs to fail fast, not retry hard.
        services.AddHttpClient<YahooQuoteProvider>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<YahooOptions>>().Value;
                client.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
                client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            })
            .RemoveAllLoggers()
            .AddHttpMessageHandler<RedactingLoggingHandler>()
            .AddResilienceHandler("yahoo", ConfigureStandardResilience);

        services.AddScoped<IQuoteProvider>(sp => sp.GetRequiredService<TwelveDataQuoteProvider>());
        services.AddScoped<IQuoteProvider>(sp => sp.GetRequiredService<CoinGeckoQuoteProvider>());
        services.AddScoped<IQuoteProvider>(sp => sp.GetRequiredService<YahooQuoteProvider>());
        services.AddScoped<IFxRateProvider>(sp => sp.GetRequiredService<TwelveDataFxProvider>());

        services.AddScoped<IQuoteProviderRouter, QuoteProviderRouter>();

        return services;
    }

    private static void ConfigureTwelveDataResilience(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        builder.AddTimeout(TimeSpan.FromSeconds(10));

        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(2),
            UseJitter = true,
            // Deliberately excludes 429: retrying a Twelve Data rate-limit response spends more
            // of the 8-req/min and 800-credit/day budget chasing a request that will likely fail
            // again immediately. Only retry on 5xx/network-level failures.
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Exception is not null ||
                (args.Outcome.Result is { } response && (int)response.StatusCode >= 500)),
        });

        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 4,
            BreakDuration = TimeSpan.FromSeconds(30),
        });
    }

    private static void ConfigureStandardResilience(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        builder.AddTimeout(TimeSpan.FromSeconds(10));

        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(1),
            UseJitter = true,
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Exception is not null ||
                (args.Outcome.Result is { } response && ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests))),
        });

        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 4,
            BreakDuration = TimeSpan.FromSeconds(30),
        });
    }

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
