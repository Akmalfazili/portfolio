using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Portfolio.Infrastructure.MarketData;

/// <summary>
/// Replaces <c>IHttpClientFactory</c>'s default request/response logging handlers (removed via
/// <c>RemoveAllLoggers()</c> on every provider client) with one that never emits a provider API
/// key. Twelve Data's key travels as an <c>apikey=</c> query-string parameter — exactly what the
/// default handlers log verbatim on both success and failure. This handler redacts that
/// parameter (and, defensively, a <c>key=</c> variant some providers use) before the URI ever
/// reaches a log line, so a failed request cannot print a live key.
/// </summary>
public sealed partial class RedactingLoggingHandler(ILogger<RedactingLoggingHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var redactedUri = Redact(request.RequestUri);

        logger.LogDebug("Sending {Method} {Uri}", request.Method, redactedUri);

        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Request {Method} {Uri} failed with status {StatusCode}",
                    request.Method,
                    redactedUri,
                    (int)response.StatusCode);
            }

            return response;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Request {Method} {Uri} threw", request.Method, redactedUri);
            throw;
        }
    }

    private static string Redact(Uri? uri)
    {
        if (uri is null)
        {
            return "(no uri)";
        }

        return SensitiveQueryParam().Replace(uri.ToString(), "$1=REDACTED");
    }

    [GeneratedRegex(@"(?i)(apikey|api_key|key)=[^&]+")]
    private static partial Regex SensitiveQueryParam();
}
