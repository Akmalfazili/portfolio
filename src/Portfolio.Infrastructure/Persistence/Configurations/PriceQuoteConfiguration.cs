using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class PriceQuoteConfiguration : IEntityTypeConfiguration<PriceQuote>
{
    public void Configure(EntityTypeBuilder<PriceQuote> builder)
    {
        builder.HasKey(q => q.Id);

        // Unit price / close-style value — decimal(28,10).
        builder.Property(q => q.Price).HasPrecision(28, 10);

        builder.Property(q => q.Currency).HasMaxLength(3).IsRequired();

        // One quote row per asset.
        builder.HasIndex(q => q.AssetId).IsUnique();
    }
}
