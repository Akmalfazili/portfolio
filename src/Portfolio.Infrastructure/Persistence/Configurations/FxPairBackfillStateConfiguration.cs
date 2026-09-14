using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class FxPairBackfillStateConfiguration : IEntityTypeConfiguration<FxPairBackfillState>
{
    public void Configure(EntityTypeBuilder<FxPairBackfillState> builder)
    {
        // Keyed by (Base, Quote) — no owning Asset, mirroring FxRate's own composite shape rather
        // than AssetPriceHistoryState/AssetDividendState's asset-id key. See the class remarks for
        // why this deliberately hangs off nothing.
        builder.HasKey(s => new { s.Base, s.Quote });

        builder.Property(s => s.Base).HasMaxLength(3).IsRequired();
        builder.Property(s => s.Quote).HasMaxLength(3).IsRequired();
        builder.Property(s => s.LastError).HasMaxLength(2000);
    }
}
