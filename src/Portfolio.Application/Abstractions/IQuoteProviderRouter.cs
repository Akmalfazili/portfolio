using Portfolio.Domain.Entities;

namespace Portfolio.Application.Abstractions;

/// <summary>
/// Dispatches an <see cref="Asset"/> to the <see cref="IQuoteProvider"/> that owns it, keyed by
/// <see cref="Portfolio.Domain.Enums.QuoteProviderKind"/> rather than <c>AssetClass</c> — stocks
/// alone now span two providers (Twelve Data for US equities, Yahoo for SGX).
/// </summary>
public interface IQuoteProviderRouter
{
    /// <summary>Resolves the provider registered for <paramref name="asset"/>'s
    /// <see cref="Asset.QuoteProviderKind"/>. Throws <see cref="InvalidOperationException"/> if
    /// no provider is registered for that kind — a configuration defect, not a runtime one.</summary>
    IQuoteProvider GetProvider(Asset asset);
}
