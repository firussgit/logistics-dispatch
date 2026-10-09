using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LogisticsDispatch.Infrastructure.Data;

public class SeedOptions
{
    public const string Section = "Seed";

    /// <summary>Create demo dispatcher / driver / customer accounts. Development convenience: leave OFF in any real deployment.</summary>
    public bool DemoUsers { get; set; }

    public string DemoPassword { get; set; } = "";
}

public static class DbSeeder
{
    public static readonly Location Center = new(40.7128, -74.0060);

    /// <summary>Name of the one human-controlled driver (answers offers through the driver page).</summary>
    public const string DemoDriverName = "You (Demo Driver)";

    public const string DemoDispatcherEmail = "dispatcher@demo.test";
    public const string DemoDriverEmail = "driver@demo.test";
    public const string DemoCustomerEmail = "customer@demo.test";

    public static async Task SeedAsync(DispatchDbContext db, IPasswordHasher<User> hasher, SeedOptions options, CancellationToken ct = default)
    {
        if (!await db.Drivers.AnyAsync(ct))
        {
            (string Name, double DLat, double DLng)[] seed =
            [
                ("Alex Rivera", 0.010, 0.012),
                ("Sam Chen", -0.015, 0.008),
                ("Jordan Blake", 0.020, -0.018),
                ("Taylor Okafor", -0.008, -0.020),
                ("Morgan Silva", 0.004, 0.025),
            ];

            foreach (var (name, dLat, dLng) in seed)
                db.Drivers.Add(new Driver { Name = name, CurrentLocation = new Location(Center.Lat + dLat, Center.Lng + dLng) });
        }

        // Make sure there is exactly one human driver, also on databases created before offers existed.
        var human = await db.Drivers.FirstOrDefaultAsync(d => !d.IsAutomated, ct);
        if (human is null)
        {
            human = new Driver { Name = DemoDriverName, IsAutomated = false, CurrentLocation = new Location(Center.Lat + 0.002, Center.Lng - 0.004) };
            db.Drivers.Add(human);
        }

        if (options.DemoUsers)
        {
            if (string.IsNullOrWhiteSpace(options.DemoPassword))
                throw new InvalidOperationException("Seed:DemoUsers is on but Seed:DemoPassword is empty.");

            await AddUserIfMissingAsync(db, hasher, options, DemoDispatcherEmail, "Dana Dispatcher", UserRole.Dispatcher, null, ct);
            await AddUserIfMissingAsync(db, hasher, options, DemoDriverEmail, "Demo Driver", UserRole.Driver, human.Id, ct);
            await AddUserIfMissingAsync(db, hasher, options, DemoCustomerEmail, "Casey Customer", UserRole.Customer, null, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task AddUserIfMissingAsync(DispatchDbContext db, IPasswordHasher<User> hasher, SeedOptions options,
        string email, string name, UserRole role, Guid? driverId, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) return;
        var user = new User { Email = email, DisplayName = name, Role = role, DriverId = driverId, CreatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = hasher.HashPassword(user, options.DemoPassword);
        db.Users.Add(user);
    }
}
