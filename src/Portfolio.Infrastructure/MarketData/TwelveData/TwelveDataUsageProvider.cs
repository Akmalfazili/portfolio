using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio.Application.Abstractions;

namespace Portfolio.Infrastructure.MarketData.TwelveData;

/// <summary>
/// Calls Twelve Data's own <c>GET /api_usage</c> — the authoritative daily credit counter, used
/// only to periodically reconcile <see cref="Portfolio.Application.Services.TwelveDataCreditThrottle"/>'s
/// persisted ledger against real spend (D39).
///
/// <b>This endpoint itself costs 1 credit per call</b> — measured live 2026-08-21 and confirmed
/// again 2026-08-24 (two consecutive calls with no intervening request moved <c>daily_usage</c> by
/// exactly 1 each time) — so it must never be polled in a tight loop; the throttle bounds how
/// often this is called.
///
/// Deliberately registered with <b>no retry policy</b> (unlike <see cref="TwelveDataQuoteProvider"/>
/// and <see cref="TwelveDataFxProvider"/>): a failed reconciliation attempt just leaves the ledger
/// as it was, and retrying would spend more of the very credit budget this call exists to measure,
/// for a monitoring read that is never on the critical path.
/// </summary>
public sealed class TwelveDataUsageProvider(
    HttpClient httpClient,
    IOptions<TwelveDataOptions> options,
    ILogger<TwelveDataUsageProvider> logger) : ITwelveDataUsageProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int?> GetDailyUsageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var requestUri = $"api_usage?apikey={options.Value.ApiKey}";
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Twelve Data /api_usage reconciliation call failed with status {StatusCode}",
                    (int)response.StatusCode);
                return null;
            }

            var payload = JsonSerializer.Deserialize<TwelveDataUsageResponse>(json, JsonOptions);
            if (payload is null)
            {
                logger.LogWarning("Twelve Data /api_usage returned an unparseable response");
                return null;
            }

            return payload.DailyUsage;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Twelve Data /api_usage reconciliation call threw");
            return null;
        }
    }
}
