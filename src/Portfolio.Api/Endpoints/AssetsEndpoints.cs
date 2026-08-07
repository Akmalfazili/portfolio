using Microsoft.AspNetCore.Http.HttpResults;
using Portfolio.Api.Binding;
using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Enums;

namespace Portfolio.Api.Endpoints;

public static class AssetsEndpoints
{
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assets").WithTags("Assets");

        // AssetClassRouteValue?, not AssetClass? — see D11: fixed on both this query parameter and
        // the Phase 6 route segment together, never just one (D7).
        group.MapGet("/", async (
            AssetClassRouteValue? assetClass,
            IAssetService assetService,
            CancellationToken cancellationToken) =>
        {
            var assets = await assetService.ListAsync(assetClass?.Value, cancellationToken);
            return TypedResults.Ok(assets);
        });

        group.MapGet("/{id:int}", async Task<Results<Ok<AssetDto>, NotFound>> (
            int id,
            IAssetService assetService,
            CancellationToken cancellationToken) =>
        {
            var asset = await assetService.GetByIdAsync(id, cancellationToken);
            return asset is null ? TypedResults.NotFound() : TypedResults.Ok(asset);
        });

        group.MapPost("/", async Task<Results<Created<AssetDto>, ValidationProblem>> (
            CreateAssetRequest request,
            IAssetService assetService,
            CancellationToken cancellationToken) =>
        {
            var result = await assetService.CreateAsync(request, cancellationToken);
            if (!result.IsSuccess)
            {
                return TypedResults.ValidationProblem(result.Error!.ValidationErrors!);
            }

            var dto = result.Value!;
            return TypedResults.Created($"/api/assets/{dto.Id}", dto);
        });

        // Full replace, including IsActive — the only way to deactivate an asset (D23/Phase 12).
        // Same D23 provider-routing coherence validation as create.
        group.MapPut("/{id:int}", async Task<Results<Ok<AssetDto>, NotFound, ValidationProblem>> (
            int id,
            UpdateAssetRequest request,
            IAssetService assetService,
            CancellationToken cancellationToken) =>
        {
            var result = await assetService.UpdateAsync(id, request, cancellationToken);
            if (result.IsSuccess)
            {
                return TypedResults.Ok(result.Value!);
            }

            return result.Error!.Kind == ServiceErrorKind.NotFound
                ? TypedResults.NotFound()
                : TypedResults.ValidationProblem(result.Error.ValidationErrors!);
        });

        // Stocks only, per the crypto scope decision. A crypto asset id returns a clean 400
        // (ValidationProblem) rather than an empty series that would render as a flat line at
        // zero; an unknown asset id returns 404.
        group.MapGet("/{id:int}/performance", async Task<Results<Ok<AssetPerformanceDto>, NotFound, ValidationProblem>> (
            int id,
            IPortfolioPerformanceService performanceService,
            CancellationToken cancellationToken) =>
        {
            var result = await performanceService.GetAssetPerformanceAsync(id, cancellationToken);
            if (result.IsSuccess)
            {
                return TypedResults.Ok(result.Value!);
            }

            return result.Error!.Kind == ServiceErrorKind.NotFound
                ? TypedResults.NotFound()
                : TypedResults.ValidationProblem(result.Error.ValidationErrors!);
        });

        return app;
    }
}
