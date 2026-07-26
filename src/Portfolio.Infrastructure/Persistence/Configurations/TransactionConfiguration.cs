using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.HasKey(t => t.Id);

        // Quantities and unit prices — decimal(28,10). Must survive fractional crypto units
        // such as 0.000123456 unchanged.
        builder.Property(t => t.Quantity).HasPrecision(28, 10);
        builder.Property(t => t.PricePerUnit).HasPrecision(28, 10);

        // Fees are a monetary total — decimal(19,4).
        builder.Property(t => t.Fees).HasPrecision(19, 4);

        builder.Property(t => t.Currency).HasMaxLength(3).IsRequired();
        builder.Property(t => t.Notes).HasMaxLength(1000);

        builder.HasIndex(t => new { t.AssetId, t.TradeDate });
    }
}
