namespace LogisticsDispatch.Core.Entities;

public class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Reference { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string? Notes { get; set; }
    public Location Pickup { get; set; }
    public Location Dropoff { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public Guid? DriverId { get; set; }
    public Location CurrentLocation { get; set; }
    public int? EtaSeconds { get; set; }
    public double Progress { get; set; }

    /// <summary>Road path driver→pickup, set shortly after assignment. JSON <c>[[lat,lng],…]</c>.</summary>
    public string? ApproachRouteJson { get; set; }

    /// <summary>Road path pickup→dropoff, set shortly after creation. JSON <c>[[lat,lng],…]</c>.</summary>
    public string? TripRouteJson { get; set; }

    /// <summary>Distance the driver has covered along <see cref="ApproachRouteJson"/>.</summary>
    public double ApproachMeters { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }

    /// <summary>Optimistic concurrency token, bumped by the DbContext on every update.</summary>
    public long Version { get; set; }

    public List<StatusHistory> History { get; set; } = [];

    public static Job Create(string customerName, string? notes, Location pickup, Location dropoff, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        var job = new Job
        {
            Id = id,
            Reference = $"JOB-{id.ToString("N")[..6].ToUpperInvariant()}",
            CustomerName = customerName,
            Notes = notes,
            Pickup = pickup,
            Dropoff = dropoff,
            CurrentLocation = pickup,
            CreatedAt = now,
            UpdatedAt = now,
        };
        job.History.Add(new StatusHistory { JobId = id, From = JobStatus.Pending, To = JobStatus.Pending, At = now, Note = "Created" });
        return job;
    }

    // State pattern: behaviour depends on the current state object.
    public void Assign(Guid driverId, DateTimeOffset now) => JobStateFactory.For(Status).Assign(this, driverId, now);
    public void StartTransit(DateTimeOffset now) => JobStateFactory.For(Status).StartTransit(this, now);
    public void Complete(DateTimeOffset now) => JobStateFactory.For(Status).Complete(this, now);
    public void Cancel(DateTimeOffset now, string? reason = null) => JobStateFactory.For(Status).Cancel(this, now, reason);

    internal void TransitionTo(JobStatus to, DateTimeOffset now, string? note = null)
    {
        History.Add(new StatusHistory { JobId = Id, From = Status, To = to, At = now, Note = note });
        Status = to;
        UpdatedAt = now;
    }
}
