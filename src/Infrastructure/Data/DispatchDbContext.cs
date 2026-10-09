using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LogisticsDispatch.Infrastructure.Data;

public class DispatchDbContext(DbContextOptions<DispatchDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<StatusHistory> StatusHistory => Set<StatusHistory>();
    public DbSet<JobOffer> Offers => Set<JobOffer>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DispatchDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite can't ORDER BY / compare DateTimeOffset as text reliably; store as sortable binary.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Optimistic concurrency: bump the version of every modified aggregate root.
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State == EntityState.Modified))
        {
            switch (entry.Entity)
            {
                case Job:
                case Driver:
                case JobOffer:
                    var prop = entry.Property("Version");
                    prop.CurrentValue = (long)prop.OriginalValue! + 1;
                    break;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
