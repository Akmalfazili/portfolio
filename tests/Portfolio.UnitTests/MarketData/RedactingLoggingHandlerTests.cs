using System.Net;
using FluentAssertions;
using Portfolio.Infrastructure.MarketData;
using Portfolio.UnitTests.TestSupport;

namespace Portfolio.UnitTests.MarketData;

/// <summary>
/// The Twelve Data key travels as an <c>apikey=</c> query-string parameter — exactly what a
/// default HTTP-client logging handler would print verbatim, especially on a failed request.
/// Proves <see cref="RedactingLoggingHandler"/> never lets that key reach a log line, on both
/// the success and the failure path.
/// </summary>
public sealed class RedactingLoggingHandlerTests
{
    private const string LiveLookingKey = "0000000000000000000000000000fake";

    private static (HttpClient Client, ListLogger<RedactingLoggingHandler> Logger) CreateSut(HttpStatusCode innerStatus)
    {
        var logger = new ListLogger<RedactingLoggingHandler>();
        var handler = new RedactingLoggingHandler(logger)
        {
            InnerHandler = new StubHttpMessageHandler(innerStatus, "{}"),
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twelvedata.com/") };
        return (client, logger);
    }

    [Fact]
    public async Task SuccessfulRequest_NeverLogsTheApiKey()
    {
        var (client, logger) = CreateSut(HttpStatusCode.OK);

        await client.GetAsync($"quote?symbol=AAPL&apikey={LiveLookingKey}");

        logger.Messages.Should().NotBeEmpty();
        logger.Messages.Should().OnlyContain(m => !m.Contains(LiveLookingKey));
    }

    [Fact]
    public async Task FailedRequest_NeverLogsTheApiKey()
    {
        // This is exactly the scenario called out as the real risk: a failed request is what
        // the default HttpClientFactory logging handlers print in full, key included.
        var (client, logger) = CreateSut(HttpStatusCode.TooManyRequests);

        await client.GetAsync($"quote?symbol=AAPL&apikey={LiveLookingKey}");

        logger.Messages.Should().Contain(m => m.Contains("429") || m.Contains("TooManyRequests"));
        logger.Messages.Should().OnlyContain(m => !m.Contains(LiveLookingKey));
    }

    [Fact]
    public async Task RedactsApiKeyQueryParameter_RegardlessOfCase()
    {
        var (client, logger) = CreateSut(HttpStatusCode.OK);

        await client.GetAsync($"quote?symbol=AAPL&ApiKey={LiveLookingKey}");

        logger.Messages.Should().Contain(m => m.Contains("REDACTED"));
        logger.Messages.Should().OnlyContain(m => !m.Contains(LiveLookingKey));
    }
}
