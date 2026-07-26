using System.Text.Json.Serialization;
using Portfolio.Api.Endpoints;
using Portfolio.Application;
using Portfolio.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();

// Enums serialize as their names ("Buy", "Crypto", ...) rather than raw ints — much friendlier
// for API consumers than a magic number that silently drifts if the enum is reordered.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "Portfolio.Api", status = "ok" }));

app.MapAssetEndpoints();
app.MapTransactionEndpoints();

app.Run();

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
