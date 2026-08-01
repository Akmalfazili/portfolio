using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Enums;

namespace Portfolio.Api.Endpoints;

/// <summary>
/// Portfolio-level calculations. <c>{assetClass}</c> works for both <see cref="AssetClass.Stock"/>
/// and <see cref="AssetClass.Crypto"/> — neither summary nor allocation needs price history.
/// Annual returns are stocks only, per the crypto scope decision, hence the fixed literal
/// <c>/stock/</c> segment rather than a class parameter.
/// </summary>
public static class PortfolioEndpoints
{
    public static IEndpointRouteBuilder MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portfolio").WithTags("Portfolio");

        group.MapGet("/{assetClass}/summary", async (
            AssetClass assetClass,
            IPortfolioSummaryService summaryService,
            CancellationToken cancellationToken) =>
        {
            var summary = await summaryService.GetSummaryAsync(assetClass, cancellationToken);
            return TypedResults.Ok(summary);
        });

        group.MapGet("/{assetClass}/allocation", async (
            AssetClass assetClass,
            IPortfolioSummaryService summaryService,
            CancellationToken cancellationToken) =>
        {
            var allocation = await summaryService.GetAllocationAsync(assetClass, cancellationToken);
            return TypedResults.Ok(allocation);
        });

        group.MapGet("/stock/annual-returns", async (
            IPortfolioPerformanceService performanceService,
            CancellationToken cancellationToken) =>
        {
            var annualReturns = await performanceService.GetAnnualReturnsAsync(cancellationToken);
            return TypedResults.Ok(annualReturns);
        });

        return app;
    }
}
