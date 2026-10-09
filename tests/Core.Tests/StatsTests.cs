using LogisticsDispatch.Core.Entities;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Services;

namespace LogisticsDispatch.Core.Tests;

public class StatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Location A = new(40.70, -74.00), B = new(40.75, -73.98);

    private static Job Delivered(Driver d, double createdMinAgo, double waitMin, double tripMin)
    {
        var created = Now.AddMinutes(-createdMinAgo);
        var j = Job.Create("c", null, A, B, created);
        j.Assign(d.Id, created.AddMinutes(waitMin));
        j.StartTransit(created.AddMinutes(waitMin));
        j.Complete(created.AddMinutes(waitMin + tripMin));
        return j;
    }

    [Fact]
    public void Averages_median_and_rates_cover_only_the_window()
    {
        var d = new Driver { Name = "Ana" };
        var jobs = new List<Job>
        {
            Delivered(d, 50, 1, 9),    // 10 min
            Delivered(d, 40, 3, 17),   // 20 min
            Delivered(d, 30, 2, 28),   // 30 min
            Delivered(d, 600, 1, 5),   // outside a 2h window
        };
        var cancelled = Job.Create("c", null, A, B, Now.AddMinutes(-20));
        cancelled.Cancel(Now.AddMinutes(-19));
        jobs.Add(cancelled);
        jobs.Add(Job.Create("c", null, A, B, Now.AddMinutes(-1)));

        var s = StatsCalculator.Compute(jobs, [], [d], Now, hours: 2);

        Assert.Equal(5, s.Orders);
        Assert.Equal(3, s.Completed);
        Assert.Equal(1, s.Cancelled);
        Assert.Equal(1, s.Active);
        Assert.Equal(0.75, s.CompletionRate);
        Assert.Equal(20, s.AvgDeliveryMinutes);
        Assert.Equal(20, s.MedianDeliveryMinutes);
        Assert.Equal(2, s.AvgWaitForDriverMinutes);
        Assert.Equal(2.5, s.OrdersPerHour);
        Assert.Equal(2, s.PerHour.Count);
        Assert.Equal(5, s.PerHour.Sum(h => h.Orders));
    }

    [Fact]
    public void Empty_data_gives_nulls_not_exceptions()
    {
        var s = StatsCalculator.Compute([], [], [], Now, 24);
        Assert.Equal(0, s.Orders);
        Assert.Null(s.AvgDeliveryMinutes);
        Assert.Null(s.MedianDeliveryMinutes);
        Assert.Null(s.CompletionRate);
        Assert.Equal(24, s.PerHour.Count);
    }

    [Fact]
    public void Drivers_are_ranked_by_completions_with_acceptance_rate()
    {
        var ana = new Driver { Name = "Ana" };
        var bo = new Driver { Name = "Bo" };
        var jobs = new List<Job> { Delivered(bo, 30, 1, 9), Delivered(bo, 20, 1, 9), Delivered(ana, 10, 1, 4) };
        List<JobOffer> offers =
        [
            new() { DriverId = bo.Id, Status = OfferStatus.Accepted, CreatedAt = Now.AddMinutes(-30) },
            new() { DriverId = bo.Id, Status = OfferStatus.Declined, CreatedAt = Now.AddMinutes(-25) },
            new() { DriverId = bo.Id, Status = OfferStatus.Cancelled, CreatedAt = Now.AddMinutes(-24) }, // not the driver's decision
            new() { DriverId = ana.Id, Status = OfferStatus.Accepted, CreatedAt = Now.AddMinutes(-10) },
        ];

        var s = StatsCalculator.Compute(jobs, offers, [ana, bo], Now, 1);

        Assert.Equal(["Bo", "Ana"], s.Drivers.Select(x => x.Name));
        Assert.Equal(2, s.Drivers[0].Completed);
        Assert.Equal(10, s.Drivers[0].AvgDeliveryMinutes);
        Assert.Equal(3, s.Drivers[0].OffersReceived);
        Assert.Equal(0.5, s.Drivers[0].AcceptanceRate);
        Assert.Equal(1.0, s.Drivers[1].AcceptanceRate);
    }

    [Fact]
    public void Rush_hour_orders_stay_in_the_covered_area_and_are_real_trips()
    {
        var orders = RushHour.Generate(40, new Random(1));
        Assert.Equal(40, orders.Count);
        foreach (var (p, d) in orders)
        {
            Assert.InRange(p.Lat, 40.68, 40.80);
            Assert.InRange(d.Lng, -74.04, -73.93);
            Assert.True(GeoMath.DistanceMeters(p, d) >= 600);
        }
    }
}
