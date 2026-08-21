using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class TwelveDataCreditLedgerEntryConfiguration : IEntityTypeConfiguration<TwelveDataCreditLedgerEntry>
{
    public void Configure(EntityTypeBuilder<TwelveDataCreditLedgerEntry> builder)
    {
        // The UTC calendar day is the key — one row per day, supplied by the app rather than
        // generated, so an upsert is a plain find-or-add on a known key (same pattern as
        // SourceRefreshStateConfiguration).
        builder.HasKey(e => e.Date);
        builder.Property(e => e.Date).ValueGeneratedNever();
    }
}
