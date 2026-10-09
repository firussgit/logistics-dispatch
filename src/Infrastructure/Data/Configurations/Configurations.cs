using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LogisticsDispatch.Infrastructure.Data.Configurations;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> b)
    {
        b.HasKey(j => j.Id);
        b.Property(j => j.Id).ValueGeneratedNever();
        b.Property(j => j.Reference).HasMaxLength(20).IsRequired();
        b.HasIndex(j => j.Reference).IsUnique();
        b.Property(j => j.CustomerName).HasMaxLength(100).IsRequired();
        b.Property(j => j.Notes).HasMaxLength(500);
        b.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(j => j.Status);
        b.HasIndex(j => j.DriverId);
        b.Property(j => j.Version).IsConcurrencyToken();
        b.ComplexProperty(j => j.Pickup);
        b.ComplexProperty(j => j.Dropoff);
        b.ComplexProperty(j => j.CurrentLocation);
        b.HasMany(j => j.History).WithOne().HasForeignKey(h => h.JobId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class DriverConfiguration : IEntityTypeConfiguration<Driver>
{
    public void Configure(EntityTypeBuilder<Driver> b)
    {
        b.HasKey(d => d.Id);
        b.Property(d => d.Id).ValueGeneratedNever();
        b.Property(d => d.Name).HasMaxLength(100).IsRequired();
        b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(d => d.Version).IsConcurrencyToken();
        b.ComplexProperty(d => d.CurrentLocation);
    }
}

public class StatusHistoryConfiguration : IEntityTypeConfiguration<StatusHistory>
{
    public void Configure(EntityTypeBuilder<StatusHistory> b)
    {
        b.HasKey(h => h.Id);
        b.Property(h => h.Id).ValueGeneratedNever();
        b.Property(h => h.From).HasConversion<string>().HasMaxLength(20);
        b.Property(h => h.To).HasConversion<string>().HasMaxLength(20);
        b.Property(h => h.Note).HasMaxLength(500);
    }
}

public class JobOfferConfiguration : IEntityTypeConfiguration<JobOffer>
{
    public void Configure(EntityTypeBuilder<JobOffer> b)
    {
        b.HasKey(o => o.Id);
        b.Property(o => o.Id).ValueGeneratedNever();
        b.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(o => o.Version).IsConcurrencyToken();
        b.HasIndex(o => new { o.JobId, o.Status });
        b.HasIndex(o => new { o.DriverId, o.Status });
        b.HasIndex(o => o.CreatedAt);
    }
}
