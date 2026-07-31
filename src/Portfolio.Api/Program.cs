using System.Text.Json.Serialization;
using Portfolio.Api.Endpoints;
using Portfolio.Api.Hubs;
using Portfolio.Application;
using Portfolio.Application.Abstractions;
using Portfolio.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();

// SignalR has its own protocol serializer — it does NOT inherit ConfigureHttpJsonOptions below,
// which only configures the minimal-API response serializer. Without this, enums cross the hub
// as raw ints ("source":0) while the REST API sends them as names ("source":"TwelveData"), the
// same field encoded two different ways in payloads the frontend has to merge. Register the same
// JsonStringEnumConverter here so the hub matches REST.
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// SignalR is the only place IPriceUpdateBroadcaster is implemented — Portfolio.Application only
// ever sees the interface, never a SignalR type.
builder.Services.AddSingleton<IPriceUpdateBroadcaster, SignalRPriceBroadcaster>();

// Enums serialize as their names ("Buy", "Crypto", ...) rather than raw ints — much friendlier
// for API consumers than a magic number that silently drifts if the enum is reordered.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "Portfolio.Api", status = "ok" }));

app.MapAssetEndpoints();
app.MapTransactionEndpoints();
app.MapPricesEndpoints();

app.MapHub<PricesHub>("/hubs/prices");

app.Run();

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
