using LogisticsDispatch.Core.Entities;
using LogisticsDispatch.Core.Services;

namespace LogisticsDispatch.Core.Tests;

public class GeoMathTests
{
    [Fact]
    public void One_degree_of_latitude_is_about_111km()
    {
        var d = GeoMath.DistanceMeters(new Location(0, 0), new Location(1, 0));
        Assert.InRange(d, 111_000, 111_400);
    }

    [Fact]
    public void Distance_to_self_is_zero() =>
        Assert.Equal(0, GeoMath.DistanceMeters(new Location(40, -74), new Location(40, -74)), 6);

    [Fact]
    public void MoveToward_moves_the_requested_distance()
    {
        var from = new Location(40, -74);
        var to = new Location(40.1, -74);
        var moved = GeoMath.MoveToward(from, to, 1000);
        Assert.InRange(GeoMath.DistanceMeters(from, moved), 995, 1005);
        Assert.InRange(GeoMath.DistanceMeters(moved, to), 0, GeoMath.DistanceMeters(from, to) - 990);
    }

    [Fact]
    public void MoveToward_snaps_to_destination_when_step_exceeds_distance()
    {
        var to = new Location(40.001, -74);
        Assert.Equal(to, GeoMath.MoveToward(new Location(40, -74), to, 10_000));
    }

    [Theory]
    [InlineData(150, 15, 10)]
    [InlineData(151, 15, 11)]
    [InlineData(0, 15, 0)]
    public void Eta_rounds_up(double meters, double speed, int expected) =>
        Assert.Equal(expected, GeoMath.EtaSeconds(meters, speed));
}
