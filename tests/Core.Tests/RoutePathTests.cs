using LogisticsDispatch.Core.Entities;
using LogisticsDispatch.Core.Routing;
using LogisticsDispatch.Core.Services;

namespace LogisticsDispatch.Core.Tests;

public class RoutePathTests
{
    // An "L": 1° north (~111 km), then 1° east at lat 1° (~111 km × cos(1°)).
    private static readonly IReadOnlyList<Location> Bent = [new(0, 0), new(1, 0), new(1, 1)];

    [Fact]
    public void Length_sums_the_segments()
    {
        var expected = GeoMath.DistanceMeters(Bent[0], Bent[1]) + GeoMath.DistanceMeters(Bent[1], Bent[2]);
        Assert.Equal(expected, RoutePath.Length(Bent), 3);
        Assert.True(RoutePath.Length(Bent) > GeoMath.DistanceMeters(Bent[0], Bent[2]), "a bent path is longer than the straight line");
    }

    [Fact]
    public void PointAt_clamps_to_both_ends()
    {
        Assert.Equal(Bent[0], RoutePath.PointAt(Bent, -5));
        Assert.Equal(Bent[0], RoutePath.PointAt(Bent, 0));
        Assert.Equal(Bent[^1], RoutePath.PointAt(Bent, RoutePath.Length(Bent) + 1_000));
    }

    [Fact]
    public void PointAt_follows_the_corner_instead_of_cutting_across()
    {
        var firstLeg = GeoMath.DistanceMeters(Bent[0], Bent[1]);

        var beforeCorner = RoutePath.PointAt(Bent, firstLeg / 2);
        Assert.Equal(0, beforeCorner.Lng, 6);                // still on the north-south leg
        Assert.InRange(beforeCorner.Lat, 0.49, 0.51);

        var pastCorner = RoutePath.PointAt(Bent, firstLeg + 50_000);
        Assert.Equal(1, pastCorner.Lat, 6);                  // now on the east-west leg
        Assert.InRange(pastCorner.Lng, 0.4, 0.5);

        var atCorner = RoutePath.PointAt(Bent, firstLeg);
        Assert.Equal(1, atCorner.Lat, 6);
        Assert.Equal(0, atCorner.Lng, 6);
    }

    [Fact]
    public void Positions_are_monotonic_along_the_path()
    {
        var total = RoutePath.Length(Bent);
        var previous = 0.0;
        for (var d = 0.0; d <= total; d += total / 40)
        {
            var p = RoutePath.PointAt(Bent, d);
            var fromStart = GeoMath.DistanceMeters(Bent[0], p);
            Assert.True(fromStart >= previous - 1, $"went backwards at {d:0}m");
            previous = fromStart;
        }
    }

    [Fact]
    public void Serialize_then_parse_round_trips()
    {
        var parsed = RoutePath.Parse(RoutePath.Serialize(Bent));

        Assert.NotNull(parsed);
        Assert.Equal(Bent.Count, parsed!.Count);
        Assert.Equal(Bent[1], parsed[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("[[1,2]]")]          // a path needs at least two points
    [InlineData("[[1,2],[3]]")]      // malformed pair
    public void Parse_rejects_missing_or_malformed_paths(string? json) => Assert.Null(RoutePath.Parse(json));

    [Fact]
    public void Length_of_json_handles_null() => Assert.Null(RoutePath.Length((string?)null));

    [Fact]
    public void StraightLine_route_has_two_points_and_the_direct_distance()
    {
        var r = Route.StraightLine(new Location(40, -74), new Location(40.1, -74));
        Assert.True(r.IsStraightLine);
        Assert.Equal(2, r.Points.Count);
        Assert.InRange(r.DistanceMeters, 11_000, 11_200);
    }
}
