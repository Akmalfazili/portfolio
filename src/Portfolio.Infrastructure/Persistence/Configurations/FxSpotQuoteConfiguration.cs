using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class FxSpotQuoteConfiguration : IEntityTypeConfiguration<FxSpotQuote>
{
    public void Configure(EntityTypeBuilder<FxSpotQuote> builder)
    {
        builder.HasKey(q => q.Id);

        // FX rate, not a quantity/price — decimal(18,8), the same convention as FxRate.Rate. NOT
        // the decimal(28,10) price precision, even though this entity otherwise mirrors PriceQuote.
        builder.Property(q => q.Rate).HasPrecision(18, 8);

        builder.Property(q => q.Base).HasMaxLength(3).IsRequired();
        builder.Property(q => q.Quote).HasMaxLength(3).IsRequired();

        // One row per currency pair.
        builder.HasIndex(q => new { q.Base, q.Quote }).IsUnique();
    }
}
