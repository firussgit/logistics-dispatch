namespace LogisticsDispatch.Core.States;

public interface IJobState
{
    JobStatus Status { get; }
    void Assign(Job job, Guid driverId, DateTimeOffset now);
    void StartTransit(Job job, DateTimeOffset now);
    void Complete(Job job, DateTimeOffset now);
    void Cancel(Job job, DateTimeOffset now, string? reason);
}

/// <summary>Every transition is illegal unless a concrete state overrides it.</summary>
public abstract class JobStateBase : IJobState
{
    public abstract JobStatus Status { get; }

    public virtual void Assign(Job job, Guid driverId, DateTimeOffset now) =>
        throw new InvalidJobTransitionException(Status, "assign");

    public virtual void StartTransit(Job job, DateTimeOffset now) =>
        throw new InvalidJobTransitionException(Status, "start transit for");

    public virtual void Complete(Job job, DateTimeOffset now) =>
        throw new InvalidJobTransitionException(Status, "complete");

    public virtual void Cancel(Job job, DateTimeOffset now, string? reason) =>
        throw new InvalidJobTransitionException(Status, "cancel");
}

public sealed class PendingState : JobStateBase
{
    public override JobStatus Status => JobStatus.Pending;

    public override void Assign(Job job, Guid driverId, DateTimeOffset now)
    {
        job.DriverId = driverId;
        job.AssignedAt = now;
        job.TransitionTo(JobStatus.Assigned, now, $"Assigned to driver {driverId}");
    }

    public override void Cancel(Job job, DateTimeOffset now, string? reason) =>
        job.TransitionTo(JobStatus.Cancelled, now, reason ?? "Cancelled");
}

public sealed class AssignedState : JobStateBase
{
    public override JobStatus Status => JobStatus.Assigned;

    public override void StartTransit(Job job, DateTimeOffset now)
    {
        job.CurrentLocation = job.Pickup;
        job.Progress = 0;
        job.TransitionTo(JobStatus.InTransit, now, "Driver en route");
    }

    public override void Cancel(Job job, DateTimeOffset now, string? reason)
    {
        job.TransitionTo(JobStatus.Cancelled, now, reason ?? "Cancelled");
        job.DriverId = null;
    }
}

public sealed class InTransitState : JobStateBase
{
    public override JobStatus Status => JobStatus.InTransit;

    public override void Complete(Job job, DateTimeOffset now)
    {
        job.CurrentLocation = job.Dropoff;
        job.Progress = 1;
        job.EtaSeconds = 0;
        job.TransitionTo(JobStatus.Completed, now, "Delivered");
    }
}

public sealed class CompletedState : JobStateBase
{
    public override JobStatus Status => JobStatus.Completed;
}

public sealed class CancelledState : JobStateBase
{
    public override JobStatus Status => JobStatus.Cancelled;
}

public static class JobStateFactory
{
    private static readonly PendingState Pending = new();
    private static readonly AssignedState Assigned = new();
    private static readonly InTransitState InTransit = new();
    private static readonly CompletedState Completed = new();
    private static readonly CancelledState Cancelled = new();

    public static IJobState For(JobStatus status) => status switch
    {
        JobStatus.Pending => Pending,
        JobStatus.Assigned => Assigned,
        JobStatus.InTransit => InTransit,
        JobStatus.Completed => Completed,
        JobStatus.Cancelled => Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
