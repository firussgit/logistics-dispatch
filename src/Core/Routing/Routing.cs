using System.Text.Json;

namespace LogisticsDispatch.Core.Routing;

/// <summary>A drivable path. <see cref="IsStraightLine"/> is true when no road network was available and we fell back to A→B.</summary>
public sealed record Route(IReadOnlyList<Location> Points, double DistanceMeters, bool IsStraightLine = false)
{
    public static Route StraightLine(Location from, Location to) =>
        new([from, to], GeoMath.DistanceMeters(from, to), IsStraightLine: true);
}

public enum RouteKind
{
    /// <summary>Driver's position at assignment → pickup.</summary>
    Approach,
    /// <summary>Pickup → dropoff.</summary>
    Trip
}

/// <summary>
/// Finds a path between two points. Implementations must not throw for "routing unavailable":
/// they return <see cref="Route.StraightLine"/> so the simulation keeps working without a routing engine.
/// </summary>
public interface IRouteProvider
{
    Task<Route> GetRouteAsync(Location from, Location to, CancellationToken ct = default);
}

/// <summary>Geometry helpers for polylines, plus the compact JSON form stored on the job (<c>[[lat,lng],…]</c>).</summary>
public static class RoutePath
{
    public static string Serialize(IReadOnlyList<Location> points) =>
        JsonSerializer.Serialize(points.Select(p => new[] { Math.Round(p.Lat, 6), Math.Round(p.Lng, 6) }));

    public static IReadOnlyList<Location>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var raw = JsonSerializer.Deserialize<double[][]>(json);
            if (raw is null || raw.Length < 2 || raw.Any(p => p.Length != 2)) return null;
            return raw.Select(p => new Location(p[0], p[1])).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static double Length(IReadOnlyList<Location> points)
    {
        double total = 0;
        for (var i = 1; i < points.Count; i++) total += GeoMath.DistanceMeters(points[i - 1], points[i]);
        return total;
    }

    /// <summary>Length of a serialized route, or null if there is none.</summary>
    public static double? Length(string? json) => Parse(json) is { } pts ? Length(pts) : null;

    /// <summary>The point <paramref name="meters"/> along the path (clamped to its ends).</summary>
    public static Location PointAt(IReadOnlyList<Location> points, double meters)
    {
        if (points.Count == 0) throw new ArgumentException("Path is empty.", nameof(points));
        if (meters <= 0) return points[0];
        var remaining = meters;
        for (var i = 1; i < points.Count; i++)
        {
            var seg = GeoMath.DistanceMeters(points[i - 1], points[i]);
            if (remaining <= seg) return seg <= 0 ? points[i] : GeoMath.MoveToward(points[i - 1], points[i], remaining);
            remaining -= seg;
        }
        return points[^1];
    }
}
