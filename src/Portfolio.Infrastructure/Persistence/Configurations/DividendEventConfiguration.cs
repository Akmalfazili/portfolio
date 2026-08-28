using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class DividendEventConfiguration : IEntityTypeConfiguration<DividendEvent>
{
    public void Configure(EntityTypeBuilder<DividendEvent> builder)
    {
        builder.HasKey(d => d.Id);

        // Per-share amount — decimal(28,10), same precision class as quantities/unit prices. The
        // default decimal(18,2) would round a small SGD payout (e.g. Z74's S$0.103) to a coarser
        // figure than the source data actually carries.
        builder.Property(d => d.AmountPerShare).HasPrecision(28, 10);

        builder.Property(d => d.Currency).HasMaxLength(3).IsRequired();

        builder.HasIndex(d => new { d.AssetId, d.ExDate }).IsUnique();
    }
}
