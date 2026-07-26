using Portfolio.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "Portfolio.Api", status = "ok" }));

app.Run();

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
