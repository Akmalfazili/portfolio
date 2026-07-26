using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class FxRateConfiguration : IEntityTypeConfiguration<FxRate>
{
    public void Configure(EntityTypeBuilder<FxRate> builder)
    {
        builder.HasKey(f => f.Id);

        // FX rates — decimal(18,8).
        builder.Property(f => f.Rate).HasPrecision(18, 8);

        builder.Property(f => f.Base).HasMaxLength(3).IsRequired();
        builder.Property(f => f.Quote).HasMaxLength(3).IsRequired();

        builder.HasIndex(f => new { f.Date, f.Base, f.Quote }).IsUnique();
    }
}
