using Microsoft.AspNetCore.Http.HttpResults;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Enums;

namespace Portfolio.Api.Endpoints;

public static class AssetsEndpoints
{
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assets").WithTags("Assets");

        group.MapGet("/", async (
            AssetClass? assetClass,
            IAssetService assetService,
            CancellationToken cancellationToken) =>
        {
            var assets = await assetService.ListAsync(assetClass, cancellationToken);
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

        return app;
    }
}
