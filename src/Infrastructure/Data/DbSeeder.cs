using Microsoft.EntityFrameworkCore;

namespace LogisticsDispatch.Infrastructure.Data;

public static class DbSeeder
{
    public static readonly Location Center = new(40.7128, -74.0060);

    /// <summary>Name of the one human-controlled driver (answers offers through the driver page).</summary>
    public const string DemoDriverName = "You (Demo Driver)";

    public static async Task SeedAsync(DispatchDbContext db, CancellationToken ct = default)
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
        if (!await db.Drivers.AnyAsync(d => !d.IsAutomated, ct))
            db.Drivers.Add(new Driver { Name = DemoDriverName, IsAutomated = false, CurrentLocation = new Location(Center.Lat + 0.002, Center.Lng - 0.004) });

        await db.SaveChangesAsync(ct);
    }
}
