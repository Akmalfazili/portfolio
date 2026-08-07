using Microsoft.AspNetCore.Http.HttpResults;
using Portfolio.Api.Binding;
using Portfolio.Application.Common;
using Portfolio.Application.Dtos;
using Portfolio.Application.Services;
using Portfolio.Domain.Enums;

namespace Portfolio.Api.Endpoints;

public static class TransactionsEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions").WithTags("Transactions");

        // AssetClassRouteValue?, not AssetClass? — see D11: the built-in enum binder is
        // case-sensitive on the query string too ("?assetClass=stock" 400ed).
        group.MapGet("/", async (
            AssetClassRouteValue? assetClass,
            int? assetId,
            ITransactionService transactionService,
            CancellationToken cancellationToken) =>
        {
            var transactions = await transactionService.ListAsync(assetClass?.Value, assetId, cancellationToken);
            return TypedResults.Ok(transactions);
        });

        group.MapGet("/{id:int}", async Task<Results<Ok<TransactionDto>, NotFound>> (
            int id,
            ITransactionService transactionService,
            CancellationToken cancellationToken) =>
        {
            var transaction = await transactionService.GetByIdAsync(id, cancellationToken);
            return transaction is null ? TypedResults.NotFound() : TypedResults.Ok(transaction);
        });

        group.MapPost("/", async Task<Results<Created<TransactionDto>, ValidationProblem>> (
            CreateTransactionRequest request,
            ITransactionService transactionService,
            CancellationToken cancellationToken) =>
        {
            var result = await transactionService.CreateAsync(request, cancellationToken);
            if (!result.IsSuccess)
            {
                return TypedResults.ValidationProblem(result.Error!.ValidationErrors!);
            }

            var dto = result.Value!;
            return TypedResults.Created($"/api/transactions/{dto.Id}", dto);
        });

        group.MapPut("/{id:int}", async Task<Results<Ok<TransactionDto>, NotFound, ValidationProblem>> (
            int id,
            UpdateTransactionRequest request,
            ITransactionService transactionService,
            CancellationToken cancellationToken) =>
        {
            var result = await transactionService.UpdateAsync(id, request, cancellationToken);
            if (result.IsSuccess)
            {
                return TypedResults.Ok(result.Value!);
            }

            return result.Error!.Kind == ServiceErrorKind.NotFound
                ? TypedResults.NotFound()
                : TypedResults.ValidationProblem(result.Error.ValidationErrors!);
        });

        group.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (
            int id,
            ITransactionService transactionService,
            CancellationToken cancellationToken) =>
        {
            var deleted = await transactionService.DeleteAsync(id, cancellationToken);
            return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
        });

        return app;
    }
}
