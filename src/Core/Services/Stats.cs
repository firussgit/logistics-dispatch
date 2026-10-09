namespace LogisticsDispatch.Core.Services;

/// <summary>Turns raw jobs/offers into the numbers on the stats page. Pure, so it is easy to test.</summary>
public static class StatsCalculator
{
    /// <summary>Everything about jobs created in the last <paramref name="hours"/> hours.</summary>
    public static StatsDto Compute(IReadOnlyList<Job> jobs, IReadOnlyList<JobOffer> offers, IReadOnlyList<Driver> drivers, DateTimeOffset now, int hours)
    {
        var since = now - TimeSpan.FromHours(hours);
        var inWindow = jobs.Where(j => j.CreatedAt >= since).ToList();
        var offersInWindow = offers.Where(o => o.CreatedAt >= since).ToList();

        var completed = inWindow.Where(j => j.Status == JobStatus.Completed).ToList();
        var cancelled = inWindow.Count(j => j.Status == JobStatus.Cancelled);
        var active = inWindow.Count(j => j.Status is JobStatus.Pending or JobStatus.Assigned or JobStatus.InTransit);
        var finished = completed.Count + cancelled;

        // A completed job is never touched again, so UpdatedAt is when it was delivered.
        var deliveryMinutes = completed.Select(j => (j.UpdatedAt - j.CreatedAt).TotalMinutes).ToList();
        var waitMinutes = inWindow.Where(j => j.AssignedAt.HasValue).Select(j => (j.AssignedAt!.Value - j.CreatedAt).TotalMinutes).ToList();

        // Hourly buckets, oldest first, the last one is the hour that ends now.
        var buckets = new int[hours];
        foreach (var j in inWindow)
        {
            var ago = (int)Math.Floor((now - j.CreatedAt).TotalHours);
            if (ago >= 0 && ago < hours) buckets[hours - 1 - ago]++;
        }
        var perHour = buckets.Select((count, i) => new HourBucket(now - TimeSpan.FromHours(hours - 1 - i), count)).ToList();

        var perDriver = drivers.Select(d =>
        {
            var mine = completed.Where(j => j.DriverId == d.Id).ToList();
            var theirOffers = offersInWindow.Where(o => o.DriverId == d.Id).ToList();
            var accepted = theirOffers.Count(o => o.Status == OfferStatus.Accepted);
            var answered = theirOffers.Count(o => o.Status is OfferStatus.Accepted or OfferStatus.Declined or OfferStatus.Expired);
            return new DriverStats(
                d.Id, d.Name, d.IsAutomated, mine.Count,
                mine.Count > 0 ? Round(mine.Average(j => (j.UpdatedAt - j.CreatedAt).TotalMinutes)) : null,
                theirOffers.Count, accepted,
                answered > 0 ? Round((double)accepted / answered) : null);
        }).OrderByDescending(d => d.Completed).ThenBy(d => d.Name).ToList();

        return new StatsDto(
            hours, now,
            inWindow.Count, completed.Count, cancelled, active,
            finished > 0 ? Round((double)completed.Count / finished) : null,
            deliveryMinutes.Count > 0 ? Round(deliveryMinutes.Average()) : null,
            deliveryMinutes.Count > 0 ? Round(Median(deliveryMinutes)) : null,
            waitMinutes.Count > 0 ? Round(waitMinutes.Average()) : null,
            hours > 0 ? Round((double)inWindow.Count / hours) : 0,
            perHour, perDriver);
    }

    private static double Round(double v) => Math.Round(v, 2);

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}

/// <summary>Random order locations for the "rush hour" demo button, kept inside the area the road router covers.</summary>
public static class RushHour
{
    public const int MaxOrders = 50;
    private const double MinLat = 40.69, MaxLat = 40.79, MinLng = -74.03, MaxLng = -73.94;
    private const double MinTripMeters = 600;

    public static IReadOnlyList<(Location Pickup, Location Dropoff)> Generate(int count, Random rng)
    {
        var result = new List<(Location, Location)>(count);
        while (result.Count < count)
        {
            var a = new Location(MinLat + rng.NextDouble() * (MaxLat - MinLat), MinLng + rng.NextDouble() * (MaxLng - MinLng));
            var b = new Location(MinLat + rng.NextDouble() * (MaxLat - MinLat), MinLng + rng.NextDouble() * (MaxLng - MinLng));
            if (GeoMath.DistanceMeters(a, b) >= MinTripMeters) result.Add((a, b));
        }
        return result;
    }
}
