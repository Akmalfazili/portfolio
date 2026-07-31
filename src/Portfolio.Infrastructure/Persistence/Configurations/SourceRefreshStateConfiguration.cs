using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class SourceRefreshStateConfiguration : IEntityTypeConfiguration<SourceRefreshState>
{
    public void Configure(EntityTypeBuilder<SourceRefreshState> builder)
    {
        // The provider kind is the key — one row per provider, supplied by the app rather than
        // generated, so an upsert is a plain find-or-add on a known key.
        builder.HasKey(s => s.Source);
        builder.Property(s => s.Source).ValueGeneratedNever();

        builder.Property(s => s.LastError).HasMaxLength(2000);
    }
}
