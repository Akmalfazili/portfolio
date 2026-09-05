using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Portfolio.Domain.Entities;

namespace Portfolio.Infrastructure.Persistence.Configurations;

public class ZakatPaymentConfiguration : IEntityTypeConfiguration<ZakatPayment>
{
    public void Configure(EntityTypeBuilder<ZakatPayment> builder)
    {
        builder.HasKey(p => p.Id);

        // Monetary total — decimal(19,4), the same convention as every other dollar figure in
        // this database. This table looks small enough to skip that step; it is not exempt.
        builder.Property(p => p.AmountSgd).HasPrecision(19, 4);

        // The only read is "history, newest first".
        builder.HasIndex(p => p.PaidOn).IsDescending();
    }
}
