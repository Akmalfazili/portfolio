using Microsoft.AspNetCore.Http.HttpResults;
using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;

namespace Portfolio.Api.Endpoints;

/// <summary>
/// Zakat on shares — see zakat.md. <c>GET /</c> is the one sanctioned exception to asset-class
/// segregation: it returns stocks and crypto in a single response because MUIS requires a single
/// grand total, but <see cref="ZakatReportDto.Stocks"/>/<see cref="ZakatReportDto.Crypto"/> stay
/// separate lists with separate subtotals so nothing aggregates implicitly. The payment endpoints
/// below are the ordinary CRUD shape used everywhere else in this API
/// (<see cref="Results{TResult1, TResult2, TResult3}"/>, never <c>IActionResult</c>).
/// </summary>
public static class ZakatEndpoints
{
    public static IEndpointRouteBuilder MapZakatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/zakat").WithTags("Zakat");

        // ?asOf= overrides "today" as the report's valuation reference date. Nisab is
        // deliberately NOT compared here — zakat.md §2.4 — so this endpoint never needs, and never
        // caches, a nisab figure.
        group.MapGet("/", async (
            DateOnly? asOf,
            IZakatService zakatService,
            CancellationToken cancellationToken) =>
        {
            var report = await zakatService.GetReportAsync(asOf, cancellationToken);
            return TypedResults.Ok(report);
        });

        group.MapGet("/payments", async (
            IZakatService zakatService,
            CancellationToken cancellationToken) =>
        {
            var payments = await zakatService.ListPaymentsAsync(cancellationToken);
            return TypedResults.Ok(payments);
        });

        group.MapPost("/payments", async Task<Results<Created<ZakatPaymentDto>, ValidationProblem>> (
            CreateZakatPaymentRequest request,
            IZakatService zakatService,
            CancellationToken cancellationToken) =>
        {
            var result = await zakatService.CreatePaymentAsync(request, cancellationToken);
            if (!result.IsSuccess)
            {
                return TypedResults.ValidationProblem(result.Error!.ValidationErrors!);
            }

            var dto = result.Value!;
            return TypedResults.Created($"/api/zakat/payments/{dto.Id}", dto);
        });

        group.MapPut("/payments/{id:int}", async Task<Results<Ok<ZakatPaymentDto>, NotFound, ValidationProblem>> (
            int id,
            UpdateZakatPaymentRequest request,
            IZakatService zakatService,
            CancellationToken cancellationToken) =>
        {
            var result = await zakatService.UpdatePaymentAsync(id, request, cancellationToken);
            if (result.IsSuccess)
            {
                return TypedResults.Ok(result.Value!);
            }

            return result.Error!.Kind == ServiceErrorKind.NotFound
                ? TypedResults.NotFound()
                : TypedResults.ValidationProblem(result.Error.ValidationErrors!);
        });

        group.MapDelete("/payments/{id:int}", async Task<Results<NoContent, NotFound>> (
            int id,
            IZakatService zakatService,
            CancellationToken cancellationToken) =>
        {
            var deleted = await zakatService.DeletePaymentAsync(id, cancellationToken);
            return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
        });

        return app;
    }
}
