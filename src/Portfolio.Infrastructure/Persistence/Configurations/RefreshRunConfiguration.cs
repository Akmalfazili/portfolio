using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class RefreshRunConfiguration : IEntityTypeConfiguration<RefreshRun>
{
    public void Configure(EntityTypeBuilder<RefreshRun> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.ErrorMessage).HasMaxLength(2000);

        builder.HasIndex(r => r.StartedAt);

        // D47: PriceBackfillService.RunIfDueAsync runs this exact filter (Trigger + Market, then
        // MAX(CompletedAt)) on every poll tick, once per market — index it explicitly rather than
        // rely on a table scan staying cheap as RefreshRuns grows.
        builder.HasIndex(r => new { r.Trigger, r.Market, r.CompletedAt });
    }
}
