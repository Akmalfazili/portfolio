using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Symbol).HasMaxLength(20).IsRequired();
        builder.Property(a => a.Name).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Exchange).HasMaxLength(20);
        builder.Property(a => a.Currency).HasMaxLength(3).IsRequired();
        builder.Property(a => a.ProviderSymbol).HasMaxLength(30);
        builder.Property(a => a.ProviderCoinId).HasMaxLength(50);

        builder.HasIndex(a => a.Symbol).IsUnique();

        builder.HasMany(a => a.Transactions)
            .WithOne(t => t.Asset)
            .HasForeignKey(t => t.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(a => a.PriceHistories)
            .WithOne(p => p.Asset)
            .HasForeignKey(p => p.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.PriceQuote)
            .WithOne(q => q.Asset)
            .HasForeignKey<PriceQuote>(q => q.AssetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
