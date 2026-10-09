namespace LogisticsDispatch.Core.Services;

public static class GeoMath
{
    private const double EarthRadiusMeters = 6_371_000;

    public static double DistanceMeters(Location a, Location b)
    {
        var dLat = ToRad(b.Lat - a.Lat);
        var dLng = ToRad(b.Lng - a.Lng);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(a.Lat)) * Math.Cos(ToRad(b.Lat)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>Moves from <paramref name="from"/> toward <paramref name="to"/> by up to <paramref name="meters"/> (straight line).</summary>
    public static Location MoveToward(Location from, Location to, double meters)
    {
        var distance = DistanceMeters(from, to);
        if (distance <= 0 || meters >= distance) return to;
        var f = meters / distance;
        return new Location(from.Lat + (to.Lat - from.Lat) * f, from.Lng + (to.Lng - from.Lng) * f);
    }

    public static int EtaSeconds(double distanceMeters, double speedMetersPerSecond) =>
        speedMetersPerSecond <= 0 ? int.MaxValue : (int)Math.Ceiling(distanceMeters / speedMetersPerSecond);

    private static double ToRad(double deg) => deg * Math.PI / 180;
}
