namespace LogisticsDispatch.Infrastructure.Simulation;

public class SimulationOptions
{
    public const string Section = "Simulation";

    /// <summary>Master switch; when false the worker does nothing.</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Real driver speed in meters/second (15 m/s ≈ 54 km/h).</summary>
    public double DriverSpeedMps { get; set; } = 15;

    /// <summary>Speeds the simulation up: each tick travels speed × interval × TimeScale meters.</summary>
    public double TimeScale { get; set; } = 1;

    /// <summary>Instantly assign Pending jobs to the nearest idle driver (no offers). Ignored when <see cref="UseOffers"/> is on.</summary>
    public bool AutoAssign { get; set; } = true;

    /// <summary>How long a job stays Assigned at minimum before the driver leaves the pickup.</summary>
    public TimeSpan PickupDwell { get; set; } = TimeSpan.FromSeconds(3);

    // ---- offer-based dispatch ----

    /// <summary>Offer each Pending job to the nearest idle driver (who can accept, decline or let it expire) instead of assigning directly.</summary>
    public bool UseOffers { get; set; }

    /// <summary>How long a driver has to answer an offer.</summary>
    public TimeSpan OfferTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>After declining/ignoring a job, a driver is not offered that same job again for this long.</summary>
    public TimeSpan DeclineCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Simulated drivers think for a random time in [min, max] before answering.</summary>
    public TimeSpan SimulatedResponseMin { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan SimulatedResponseMax { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>Probability (0–1) that a simulated driver accepts an offer.</summary>
    public double SimulatedAcceptRate { get; set; } = 0.75;
}
