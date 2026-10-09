namespace LogisticsDispatch.Core.Entities;

public class Driver
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DriverStatus Status { get; set; } = DriverStatus.Idle;
    public Location CurrentLocation { get; set; }
    public Guid? ActiveJobId { get; set; }

    /// <summary>Simulated drivers answer job offers on their own; a human driver answers through the driver page.</summary>
    public bool IsAutomated { get; set; } = true;

    /// <summary>Optimistic concurrency token, bumped by the DbContext on every update.</summary>
    public long Version { get; set; }

    public void MarkBusy(Guid jobId)
    {
        if (Status != DriverStatus.Idle)
            throw new DriverUnavailableException(Id, Status);
        Status = DriverStatus.Busy;
        ActiveJobId = jobId;
    }

    public void MarkIdle()
    {
        Status = DriverStatus.Idle;
        ActiveJobId = null;
    }
}
