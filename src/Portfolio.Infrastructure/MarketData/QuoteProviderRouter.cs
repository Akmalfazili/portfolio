using Portfolio.Application.Abstractions;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.MarketData;

/// <summary>See <see cref="IQuoteProviderRouter"/>. Providers are registered once at DI startup
/// (one per <see cref="Portfolio.Domain.Enums.QuoteProviderKind"/>) and looked up by the asset's
/// own <see cref="Asset.QuoteProviderKind"/> — an explicit, data-driven dispatch rather than
/// inferring the provider from currency, exchange, or symbol shape.</summary>
public sealed class QuoteProviderRouter(IEnumerable<IQuoteProvider> providers) : IQuoteProviderRouter
{
    private readonly Dictionary<Domain.Enums.QuoteProviderKind, IQuoteProvider> _byKind =
        providers.ToDictionary(p => p.Kind);

    public IQuoteProvider GetProvider(Asset asset)
    {
        if (!_byKind.TryGetValue(asset.QuoteProviderKind, out var provider))
        {
            throw new InvalidOperationException(
                $"No IQuoteProvider registered for {asset.QuoteProviderKind} (asset {asset.Id}, {asset.Symbol}).");
        }

        return provider;
    }
}
