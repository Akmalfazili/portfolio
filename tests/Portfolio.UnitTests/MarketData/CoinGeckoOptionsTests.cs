using FluentAssertions;
using Portfolio.Infrastructure.MarketData.CoinGecko;

namespace Portfolio.UnitTests.MarketData;

/// <summary>
/// Pins D32: the CoinGecko Pro-vs-keyless routing must go through
/// <see cref="CoinGeckoOptions.HasApiKey"/>, never a bare <c>ApiKey is null</c> check.
///
/// <c>dotnet user-secrets</c> leaves an unset key genuinely absent (binds to <c>null</c>), but a
/// compose <c>.env</c> file that names <c>CoinGecko__ApiKey</c> with a blank value — exactly what
/// the free-tier setup instructs — binds the environment variable to <c>""</c>: present, not
/// absent. `ApiKey is null` treats that as "a key is configured" and routes to the paid Pro API
/// with no key attached, which 401s. This was found live against the real Docker stack on
/// 2026-08-09 (`docker compose logs api` showed 401s against pro-api.coingecko.com for the whole
/// session) before it was fixed here.
/// </summary>
public sealed class CoinGeckoOptionsTests
{
    [Fact]
    public void HasApiKey_IsFalse_WhenApiKeyIsNull()
    {
        // The dotnet user-secrets shape: an unset key is genuinely absent.
        new CoinGeckoOptions { ApiKey = null }.HasApiKey.Should().BeFalse();
    }

    [Fact]
    public void HasApiKey_IsFalse_WhenApiKeyIsEmpty()
    {
        // The compose .env shape that caused D32: `CoinGecko__ApiKey=` binds to "", not null.
        new CoinGeckoOptions { ApiKey = "" }.HasApiKey.Should().BeFalse(
            "an empty string is present-but-empty, not a configured key, and must route to the " +
            "free keyless API rather than the paid Pro API with no key attached");
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \n  ")]
    public void HasApiKey_IsFalse_WhenApiKeyIsWhitespaceOnly(string whitespace)
    {
        new CoinGeckoOptions { ApiKey = whitespace }.HasApiKey.Should().BeFalse();
    }

    [Fact]
    public void HasApiKey_IsTrue_WhenApiKeyIsAGenuineValue()
    {
        new CoinGeckoOptions { ApiKey = "cg-demo-abc123" }.HasApiKey.Should().BeTrue();
    }
}
