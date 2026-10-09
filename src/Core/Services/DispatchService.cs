namespace LogisticsDispatch.Core.Services;

/// <summary>Orchestrates job lifecycle use-cases: mutate → save → notify (only after a successful save).</summary>
public class DispatchService(
    IJobRepository jobs,
    IDriverRepository drivers,
    IUnitOfWork uow,
    IDispatchNotifier notifier,
    TimeProvider time)
{
    public async Task<JobDto> CreateJobAsync(string customerName, string? notes, Location pickup, Location dropoff, CancellationToken ct = default)
    {
        var job = Job.Create(customerName, notes, pickup, dropoff, time.GetUtcNow());
        jobs.Add(job);
        await uow.SaveChangesAsync(ct);

        var dto = JobDto.From(job);
        await notifier.JobCreatedAsync(dto, ct);
        return dto;
    }

    public async Task<JobDto> AssignAsync(Guid jobId, Guid driverId, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        var driver = await drivers.GetAsync(driverId, ct) ?? throw new NotFoundException("Driver", driverId);

        // Validate before mutating anything so a rejected request leaves both aggregates untouched.
        if (driver.Status != DriverStatus.Idle) throw new DriverUnavailableException(driver.Id, driver.Status);
        job.Assign(driverId, time.GetUtcNow());
        driver.MarkBusy(job.Id);
        await uow.SaveChangesAsync(ct);

        await PublishStatusAsync(job, ct);
        await notifier.DriverUpdatedAsync(DriverDto.From(driver), ct);
        return JobDto.From(job);
    }

    public async Task<JobDto> StartTransitAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        job.StartTransit(time.GetUtcNow());

        var driver = await RequireDriverAsync(job, ct);
        driver.CurrentLocation = job.Pickup;
        job.EtaSeconds = null;
        await uow.SaveChangesAsync(ct);

        await PublishStatusAsync(job, ct);
        await notifier.DriverUpdatedAsync(DriverDto.From(driver), ct);
        return JobDto.From(job);
    }

    /// <summary>Records a GPS tick while the assigned driver is driving to the pickup point.</summary>
    public async Task MoveToPickupAsync(Guid jobId, Location location, int etaSeconds, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        if (job.Status != JobStatus.Assigned)
            throw new InvalidJobTransitionException(job.Status, "move to pickup for");

        var driver = await RequireDriverAsync(job, ct);
        driver.CurrentLocation = location;
        job.CurrentLocation = location;
        job.EtaSeconds = etaSeconds;
        job.UpdatedAt = time.GetUtcNow();
        await uow.SaveChangesAsync(ct);

        await notifier.JobProgressAsync(new JobProgressEvent(job.Id, location.Lat, location.Lng, etaSeconds, 0), ct);
    }

    /// <summary>Records a GPS/progress tick for an in-transit job and moves its driver.</summary>
    public async Task UpdateProgressAsync(Guid jobId, Location location, int etaSeconds, double progress, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        if (job.Status != JobStatus.InTransit)
            throw new InvalidJobTransitionException(job.Status, "update progress of");

        var driver = await RequireDriverAsync(job, ct);
        job.CurrentLocation = location;
        job.EtaSeconds = etaSeconds;
        job.Progress = Math.Clamp(progress, 0, 1);
        job.UpdatedAt = time.GetUtcNow();
        driver.CurrentLocation = location;
        await uow.SaveChangesAsync(ct);

        await notifier.JobProgressAsync(new JobProgressEvent(job.Id, location.Lat, location.Lng, etaSeconds, job.Progress), ct);
    }

    public async Task<JobDto> CompleteAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        job.Complete(time.GetUtcNow());

        var driver = await RequireDriverAsync(job, ct);
        driver.CurrentLocation = job.Dropoff;
        driver.MarkIdle();
        await uow.SaveChangesAsync(ct);

        await PublishStatusAsync(job, ct);
        await notifier.DriverUpdatedAsync(DriverDto.From(driver), ct);
        return JobDto.From(job);
    }

    public async Task<JobDto> CancelAsync(Guid jobId, string? reason = null, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        var driverId = job.DriverId;
        job.Cancel(time.GetUtcNow(), reason);

        Driver? driver = null;
        if (driverId is { } id)
        {
            driver = await drivers.GetAsync(id, ct);
            driver?.MarkIdle();
        }
        await uow.SaveChangesAsync(ct);

        await PublishStatusAsync(job, ct);
        if (driver is not null) await notifier.DriverUpdatedAsync(DriverDto.From(driver), ct);
        return JobDto.From(job);
    }

    public async Task<JobDto> GetJobDetailAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await jobs.GetWithHistoryAsync(jobId, ct) ?? throw new NotFoundException("Job", jobId);
        return JobDto.From(job, includeHistory: true);
    }

    private async Task<Job> GetJobAsync(Guid id, CancellationToken ct) =>
        await jobs.GetAsync(id, ct) ?? throw new NotFoundException("Job", id);

    private async Task<Driver> RequireDriverAsync(Job job, CancellationToken ct) =>
        job.DriverId is { } id
            ? await drivers.GetAsync(id, ct) ?? throw new NotFoundException("Driver", id)
            : throw new InvalidOperationException($"Job {job.Id} has no driver.");

    private Task PublishStatusAsync(Job job, CancellationToken ct) =>
        notifier.JobStatusChangedAsync(new JobStatusChangedEvent(job.Id, job.Reference, job.Status, job.DriverId, job.UpdatedAt), ct);
}
