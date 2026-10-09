using Microsoft.EntityFrameworkCore;

namespace LogisticsDispatch.Infrastructure.Data;

public static class DbSeeder
{
    public static readonly Location Center = new(40.7128, -74.0060);

    public static async Task SeedAsync(DispatchDbContext db, CancellationToken ct = default)
    {
        if (await db.Drivers.AnyAsync(ct)) return;

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

        await db.SaveChangesAsync(ct);
    }
}
