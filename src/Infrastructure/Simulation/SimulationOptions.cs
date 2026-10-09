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

    /// <summary>Automatically assign Pending jobs to the nearest idle driver.</summary>
    public bool AutoAssign { get; set; } = true;

    /// <summary>How long a job stays Assigned before the driver "accepts" and starts moving.</summary>
    public TimeSpan PickupDwell { get; set; } = TimeSpan.FromSeconds(3);
}
