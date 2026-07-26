using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class PriceHistoryConfiguration : IEntityTypeConfiguration<PriceHistory>
{
    public void Configure(EntityTypeBuilder<PriceHistory> builder)
    {
        builder.HasKey(p => p.Id);

        // Daily close — decimal(28,10).
        builder.Property(p => p.Close).HasPrecision(28, 10);

        builder.Property(p => p.Currency).HasMaxLength(3).IsRequired();

        builder.HasIndex(p => new { p.AssetId, p.Date }).IsUnique();
    }
}
