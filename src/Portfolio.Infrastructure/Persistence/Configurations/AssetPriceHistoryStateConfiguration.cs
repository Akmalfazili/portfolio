using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class AssetPriceHistoryStateConfiguration : IEntityTypeConfiguration<AssetPriceHistoryState>
{
    public void Configure(EntityTypeBuilder<AssetPriceHistoryState> builder)
    {
        // The asset id is the key — one row per stock asset, supplied by the app rather than
        // generated, same upsert-on-a-known-key shape as AssetDividendStateConfiguration.
        builder.HasKey(s => s.AssetId);
        builder.Property(s => s.AssetId).ValueGeneratedNever();

        builder.Property(s => s.LastError).HasMaxLength(2000);

        // CoveredFrom/CoveredTo are DateOnly, not decimal — no explicit precision configuration
        // needed (CLAUDE.md's decimal-precision rule does not apply to date columns).
    }
}
