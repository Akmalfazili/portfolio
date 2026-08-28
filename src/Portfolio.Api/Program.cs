using Portfolio.Api.Endpoints;
using Portfolio.Api.Hubs;
using Portfolio.Api.Serialization;
using Portfolio.Application;
using Portfolio.Application.Abstractions;
using Portfolio.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();

// D7: both JSON serializers this process runs are built from ONE definition. SignalR does not
// inherit ConfigureHttpJsonOptions — it has an entirely separate serializer — so these two calls
// are the only places the contract is applied, and PortfolioJsonSerialization.Apply is the only
// place it is decided. JsonSerializationParityTests asserts the two produce byte-identical JSON.
builder.Services.AddSignalR()
    .AddJsonProtocol(options => PortfolioJsonSerialization.Apply(options.PayloadSerializerOptions));
// SignalR is the only place IPriceUpdateBroadcaster is implemented — Portfolio.Application only
// ever sees the interface, never a SignalR type.
builder.Services.AddSingleton<IPriceUpdateBroadcaster, SignalRPriceBroadcaster>();

builder.Services.ConfigureHttpJsonOptions(options => PortfolioJsonSerialization.Apply(options.SerializerOptions));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "Portfolio.Api", status = "ok" }));

app.MapAssetEndpoints();
app.MapTransactionEndpoints();
app.MapPricesEndpoints();
app.MapDividendEndpoints();
app.MapPortfolioEndpoints();

app.MapHub<PricesHub>("/hubs/prices");

app.Run();

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
