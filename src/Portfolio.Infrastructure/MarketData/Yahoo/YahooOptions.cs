namespace Portfolio.Infrastructure.MarketData.Yahoo;

/// <summary>
/// Yahoo's chart endpoint needs no key or signup, but is unofficial and undocumented (no SLA) —
/// treat it as fallible everywhere it is called. It also requires a browser-like User-Agent or
/// it can 403; that header is set once on the shared <c>HttpClient</c> in DI, not per-request.
/// </summary>
public sealed class YahooOptions
{
    public const string SectionName = "Yahoo";

    public string BaseUrl { get; set; } = "https://query1.finance.yahoo.com";

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";
}
