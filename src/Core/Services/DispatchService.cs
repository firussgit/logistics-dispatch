namespace LogisticsDispatch.Core.Services;

/// <summary>Orchestrates job lifecycle use-cases: mutate → save → notify (only after a successful save).</summary>
public class DispatchService(
    IJobRepository jobs,
    IDriverRepository drivers,
    IOfferRepository offers,
    IUnitOfWork uow,
    IDispatchNotifier notifier,
    TimeProvider time)
{
    public async Task<JobDto> CreateJobAsync(string customerName, string? notes, Location pickup, Location dropoff, Guid? customerId = null, CancellationToken ct = default)
    {
        var job = Job.Create(customerName, notes, pickup, dropoff, time.GetUtcNow(), customerId);
        jobs.Add(job);
        await uow.SaveChangesAsync(ct);

        var dto = JobDto.From(job);
        await notifier.JobCreatedAsync(dto, ct);
        return dto;
    }

    // ---------------------------------------------------------------- offers

    /// <summary>Proposes a Pending job to one idle driver for a limited time.</summary>
    public async Task<OfferDto> OfferJobAsync(Guid jobId, Guid driverId, TimeSpan ttl, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        var driver = await drivers.GetAsync(driverId, ct) ?? throw new NotFoundException("Driver", driverId);
        var now = time.GetUtcNow();

        if (job.Status != JobStatus.Pending) throw new InvalidJobTransitionException(job.Status, "offer");
        if (driver.Status != DriverStatus.Idle) throw new DriverUnavailableException(driver.Id, driver.Status);
        if ((await offers.ListForJobAsync(jobId, ct)).Any(o => o.Status == OfferStatus.Pending && o.ExpiresAt > now))
            throw new ConcurrencyConflictException($"Job {job.Reference} already has an open offer.");

        var offer = JobOffer.Create(jobId, driverId, now, ttl);
        offers.Add(offer);
        await uow.SaveChangesAsync(ct);

        var dto = OfferDto.From(offer, job, driver);
        await notifier.OfferCreatedAsync(dto, ct);
        return dto;
    }

    /// <summary>The driver takes the job: offer accepted, job assigned, driver busy, competing offers withdrawn.</summary>
    public async Task<JobDto> AcceptOfferAsync(Guid offerId, CancellationToken ct = default)
    {
        var offer = await GetOfferAsync(offerId, ct);
        var job = await GetJobAsync(offer.JobId, ct);
        var driver = await drivers.GetAsync(offer.DriverId, ct) ?? throw new NotFoundException("Driver", offer.DriverId);
        var now = time.GetUtcNow();

        // Validate everything before mutating so a rejected accept changes nothing.
        if (offer.Status != OfferStatus.Pending || now >= offer.ExpiresAt) { offer.Accept(now); /* throws OfferNotActive */ }
        if (job.Status != JobStatus.Pending) throw new InvalidJobTransitionException(job.Status, "assign");
        if (driver.Status != DriverStatus.Idle) throw new DriverUnavailableException(driver.Id, driver.Status);

        offer.Accept(now);
        job.Assign(driver.Id, now);
        driver.MarkBusy(job.Id);
        var withdrawn = await CancelOpenOffersAsync(job.Id, except: offer.Id, now, ct);
        await uow.SaveChangesAsync(ct);

        await PublishOfferAsync(offer, job, driver, ct);
        foreach (var o in withdrawn) await PublishOfferAsync(o, job, null, ct);
        await PublishStatusAsync(job, ct);
        await notifier.DriverUpdatedAsync(DriverDto.From(driver), ct);
        return JobDto.From(job);
    }

    public async Task<OfferDto> DeclineOfferAsync(Guid offerId, CancellationToken ct = default)
    {
        var offer = await GetOfferAsync(offerId, ct);
        offer.Decline(time.GetUtcNow());
        await uow.SaveChangesAsync(ct);
        return await PublishOfferAsync(offer, null, null, ct);
    }

    /// <summary>Marks an unanswered offer as timed out. Safe to call on an offer that was already answered.</summary>
    public async Task ExpireOfferAsync(Guid offerId, CancellationToken ct = default)
    {
        var offer = await GetOfferAsync(offerId, ct);
        if (!offer.Expire(time.GetUtcNow())) return;
        await uow.SaveChangesAsync(ct);
        await PublishOfferAsync(offer, null, null, ct);
    }

    public async Task<IReadOnlyList<OfferDto>> ListOffersAsync(OfferStatus? status, Guid? driverId, CancellationToken ct = default)
    {
        var list = await offers.ListAsync(status, driverId, ct);
        var result = new List<OfferDto>(list.Count);
        foreach (var o in list) result.Add(await BuildOfferDtoAsync(o, null, null, ct));
        return result;
    }

    // ------------------------------------------------------------ job steps

    public async Task<JobDto> AssignAsync(Guid jobId, Guid driverId, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        var driver = await drivers.GetAsync(driverId, ct) ?? throw new NotFoundException("Driver", driverId);

        // Validate before mutating anything so a rejected request leaves both aggregates untouched.
        if (driver.Status != DriverStatus.Idle) throw new DriverUnavailableException(driver.Id, driver.Status);
        var now = time.GetUtcNow();
        job.Assign(driverId, now);
        driver.MarkBusy(job.Id);
        var withdrawn = await CancelOpenOffersAsync(job.Id, except: null, now, ct);
        await uow.SaveChangesAsync(ct);

        foreach (var o in withdrawn) await PublishOfferAsync(o, job, null, ct);
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
    public async Task MoveToPickupAsync(Guid jobId, Location location, int etaSeconds, double approachMeters, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        if (job.Status != JobStatus.Assigned)
            throw new InvalidJobTransitionException(job.Status, "move to pickup for");

        var driver = await RequireDriverAsync(job, ct);
        driver.CurrentLocation = location;
        job.CurrentLocation = location;
        job.EtaSeconds = etaSeconds;
        job.ApproachMeters = approachMeters;
        job.UpdatedAt = time.GetUtcNow();
        await uow.SaveChangesAsync(ct);

        await notifier.JobProgressAsync(new JobProgressEvent(job.Id, location.Lat, location.Lng, etaSeconds, 0, job.DriverId, job.CustomerId), ct);
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

        await notifier.JobProgressAsync(new JobProgressEvent(job.Id, location.Lat, location.Lng, etaSeconds, job.Progress, job.DriverId, job.CustomerId), ct);
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
        var now = time.GetUtcNow();
        job.Cancel(now, reason);

        Driver? driver = null;
        if (driverId is { } id)
        {
            driver = await drivers.GetAsync(id, ct);
            driver?.MarkIdle();
        }
        var withdrawn = await CancelOpenOffersAsync(job.Id, except: null, now, ct);
        await uow.SaveChangesAsync(ct);

        foreach (var o in withdrawn) await PublishOfferAsync(o, job, null, ct);
        await PublishStatusAsync(job, ct);
        if (driver is not null) await notifier.DriverUpdatedAsync(DriverDto.From(driver), ct);
        return JobDto.From(job);
    }

    // --------------------------------------------------------------- routes

    /// <summary>Stores a road path for one leg of the job and tells clients to (re)load it.</summary>
    public async Task SetRouteAsync(Guid jobId, RouteKind kind, Route route, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        if (job.Status is JobStatus.Completed or JobStatus.Cancelled)
            throw new InvalidJobTransitionException(job.Status, "set a route for");
        if (kind == RouteKind.Approach && job.Status != JobStatus.Assigned)
            throw new InvalidJobTransitionException(job.Status, "set an approach route for");

        var json = RoutePath.Serialize(route.Points);
        if (kind == RouteKind.Approach)
        {
            job.ApproachRouteJson = json;
            job.ApproachMeters = 0;
        }
        else
        {
            job.TripRouteJson = json;
        }
        await uow.SaveChangesAsync(ct);
        await notifier.RouteReadyAsync(new RouteReadyEvent(job.Id, kind, job.DriverId, job.CustomerId), ct);
    }

    public async Task<RouteDto> GetRouteAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        static IReadOnlyList<double[]>? Pairs(string? json) =>
            RoutePath.Parse(json)?.Select(p => new[] { p.Lat, p.Lng }).ToList();

        var trip = RoutePath.Parse(job.TripRouteJson);
        // A two-point trip route means routing was unavailable and we fell back to a straight line.
        return new RouteDto(job.Id, Pairs(job.ApproachRouteJson), Pairs(job.TripRouteJson), trip is { Count: 2 });
    }

    /// <summary>Resolves a public tracking link. Returns null for an unknown or malformed token (never throws, never says why).</summary>
    public async Task<TrackingDto?> GetTrackingAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 32) return null;
        var job = await jobs.GetByTrackingTokenAsync(token.ToLowerInvariant(), ct);
        if (job is null) return null;

        var driver = job.DriverId is { } id ? await drivers.GetAsync(id, ct) : null;
        var history = job.History.OrderBy(h => h.At).Select(h => new StatusHistoryDto(h.From, h.To, h.At, h.Note)).ToList();
        return new TrackingDto(job.Id, job.Reference, job.Status, driver?.Name, driver?.CurrentLocation, job.Pickup, job.Dropoff,
            job.CurrentLocation, job.EtaSeconds, job.Progress, job.CreatedAt, history);
    }

    public async Task<JobDto> GetJobDetailAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await jobs.GetWithHistoryAsync(jobId, ct) ?? throw new NotFoundException("Job", jobId);
        return JobDto.From(job, includeHistory: true);
    }

    // -------------------------------------------------------------- helpers

    private async Task<Job> GetJobAsync(Guid id, CancellationToken ct) =>
        await jobs.GetAsync(id, ct) ?? throw new NotFoundException("Job", id);

    private async Task<JobOffer> GetOfferAsync(Guid id, CancellationToken ct) =>
        await offers.GetAsync(id, ct) ?? throw new NotFoundException("Offer", id);

    private async Task<Driver> RequireDriverAsync(Job job, CancellationToken ct) =>
        job.DriverId is { } id
            ? await drivers.GetAsync(id, ct) ?? throw new NotFoundException("Driver", id)
            : throw new InvalidOperationException($"Job {job.Id} has no driver.");

    /// <summary>Withdraws every still-open offer on a job (job assigned, taken, or cancelled).</summary>
    private async Task<List<JobOffer>> CancelOpenOffersAsync(Guid jobId, Guid? except, DateTimeOffset now, CancellationToken ct)
    {
        var withdrawn = new List<JobOffer>();
        foreach (var o in await offers.ListForJobAsync(jobId, ct))
            if (o.Id != except && o.Cancel(now)) withdrawn.Add(o);
        return withdrawn;
    }

    private async Task<OfferDto> BuildOfferDtoAsync(JobOffer offer, Job? job, Driver? driver, CancellationToken ct)
    {
        job ??= await GetJobAsync(offer.JobId, ct);
        driver ??= await drivers.GetAsync(offer.DriverId, ct) ?? throw new NotFoundException("Driver", offer.DriverId);
        return OfferDto.From(offer, job, driver);
    }

    private async Task<OfferDto> PublishOfferAsync(JobOffer offer, Job? job, Driver? driver, CancellationToken ct)
    {
        var dto = await BuildOfferDtoAsync(offer, job?.Id == offer.JobId ? job : null, driver?.Id == offer.DriverId ? driver : null, ct);
        await notifier.OfferUpdatedAsync(dto, ct);
        return dto;
    }

    private Task PublishStatusAsync(Job job, CancellationToken ct) =>
        notifier.JobStatusChangedAsync(new JobStatusChangedEvent(job.Id, job.Reference, job.Status, job.DriverId, job.UpdatedAt, job.CustomerId), ct);
}
