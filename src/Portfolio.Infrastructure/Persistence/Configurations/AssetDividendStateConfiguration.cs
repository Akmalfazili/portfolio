using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class AssetDividendStateConfiguration : IEntityTypeConfiguration<AssetDividendState>
{
    public void Configure(EntityTypeBuilder<AssetDividendState> builder)
    {
        // The asset id is the key — one row per stock asset, supplied by the app rather than
        // generated, same upsert-on-a-known-key shape as SourceRefreshStateConfiguration.
        builder.HasKey(s => s.AssetId);
        builder.Property(s => s.AssetId).ValueGeneratedNever();

        builder.Property(s => s.LastError).HasMaxLength(2000);
    }
}
