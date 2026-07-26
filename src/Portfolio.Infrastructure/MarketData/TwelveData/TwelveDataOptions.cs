namespace Portfolio.Infrastructure.MarketData.TwelveData;

/// <summary>
/// <c>ApiKey</c> is never set in <c>appsettings.json</c> — it comes from <c>dotnet user-secrets</c>
/// locally (<c>TwelveData:ApiKey</c> under Portfolio.Api) and the <c>TwelveData__ApiKey</c>
/// environment variable in containers.
/// </summary>
public sealed class TwelveDataOptions
{
    public const string SectionName = "TwelveData";

    public required string ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.twelvedata.com";
}
