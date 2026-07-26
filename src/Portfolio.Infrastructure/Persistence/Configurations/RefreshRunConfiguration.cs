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
    }
}
