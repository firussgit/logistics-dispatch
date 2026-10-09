using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogisticsDispatch.Infrastructure.Simulation;

/// <summary>
/// Singleton background service that drives the simulated fleet. It never holds a DbContext:
/// every unit of work runs in its own DI scope so one failure can't poison later work.
/// </summary>
public class SimulationWorker(
    IServiceScopeFactory scopes,
    IOptions<SimulationOptions> options,
    TimeProvider time,
    ILogger<SimulationWorker> logger) : BackgroundService
{
    private static readonly Type[] ExpectedConflicts =
        [typeof(ConcurrencyConflictException), typeof(InvalidJobTransitionException), typeof(DriverUnavailableException), typeof(NotFoundException), typeof(OfferNotActiveException)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Simulation worker disabled.");
            return;
        }

        logger.LogInformation("Simulation worker started (tick {Tick}, speed {Speed} m/s, x{Scale}).", opts.TickInterval, opts.DriverSpeedMps, opts.TimeScale);
        using var timer = new PeriodicTimer(opts.TickInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Simulation tick failed; continuing.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        logger.LogInformation("Simulation worker stopped.");
    }

    /// <summary>One simulation step. Public so tests can drive it deterministically.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var opts = options.Value;
        if (opts.UseOffers) await DispatchOffersAsync(opts, ct);
        else if (opts.AutoAssign) await AutoAssignAsync(ct);
        await ApproachPickupAsync(opts, ct);
        await MoveInTransitAsync(opts, ct);
    }

    /// <summary>
    /// Offer-based dispatch: (1) expire unanswered offers, (2) let simulated drivers answer, (3) offer each
    /// Pending job to the nearest idle driver who isn't reserved or cooling down for that job.
    /// </summary>
    private async Task DispatchOffersAsync(SimulationOptions opts, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var window = now - (opts.OfferTimeout + opts.DeclineCooldown);

        // 1 + 2: sweep open offers
        List<JobOffer> open;
        Dictionary<Guid, Driver> driversById;
        using (var scope = scopes.CreateScope())
        {
            open = (await scope.ServiceProvider.GetRequiredService<IOfferRepository>().ListAsync(OfferStatus.Pending, null, ct)).ToList();
            driversById = (await scope.ServiceProvider.GetRequiredService<IDriverRepository>().ListAsync(ct)).ToDictionary(d => d.Id);
        }

        foreach (var offer in open)
        {
            if (offer.ExpiresAt <= now)
            {
                await RunAsync(offer.JobId, s => s.ExpireOfferAsync(offer.Id, ct));
                continue;
            }
            if (!driversById.TryGetValue(offer.DriverId, out var driver) || !driver.IsAutomated) continue;

            var (delay, accepts) = SimulatedDecision(offer.Id, opts);
            if (now - offer.CreatedAt < delay) continue;
            await RunAsync(offer.JobId, s => accepts ? s.AcceptOfferAsync(offer.Id, ct) : s.DeclineOfferAsync(offer.Id, ct));
        }

        // 3: make new offers
        List<Job> pending;
        List<JobOffer> recent;
        List<Driver> idle;
        using (var scope = scopes.CreateScope())
        {
            var sp = scope.ServiceProvider;
            pending = (await sp.GetRequiredService<IJobRepository>().ListAsync(JobStatus.Pending, ct)).Reverse().ToList();
            recent = (await sp.GetRequiredService<IOfferRepository>().ListSinceAsync(window, ct)).ToList();
            idle = (await sp.GetRequiredService<IDriverRepository>().GetIdleAsync(ct)).ToList();
        }

        var reserved = recent.Where(o => o.Status == OfferStatus.Pending).Select(o => o.DriverId).ToHashSet();
        foreach (var job in pending)
        {
            if (recent.Any(o => o.JobId == job.Id && o.Status == OfferStatus.Pending)) continue; // already out for an answer

            var candidate = idle
                .Where(d => !reserved.Contains(d.Id))
                .Where(d => !recent.Any(o => o.JobId == job.Id && o.DriverId == d.Id &&
                                             o.Status is OfferStatus.Declined or OfferStatus.Expired &&
                                             now - (o.RespondedAt ?? o.ExpiresAt) < opts.DeclineCooldown))
                .MinBy(d => GeoMath.DistanceMeters(d.CurrentLocation, job.Pickup));
            if (candidate is null) continue;

            reserved.Add(candidate.Id);
            await RunAsync(job.Id, s => s.OfferJobAsync(job.Id, candidate.Id, opts.OfferTimeout, ct));
        }
    }

    /// <summary>Deterministic per-offer "personality" so a simulated driver's behaviour is stable and testable.</summary>
    private static (TimeSpan Delay, bool Accepts) SimulatedDecision(Guid offerId, SimulationOptions opts)
    {
        var bytes = offerId.ToByteArray();
        var a = BitConverter.ToUInt32(bytes, 0) / (double)uint.MaxValue;
        var b = BitConverter.ToUInt32(bytes, 4) / (double)uint.MaxValue;
        var span = opts.SimulatedResponseMax - opts.SimulatedResponseMin;
        return (opts.SimulatedResponseMin + span * a, b < opts.SimulatedAcceptRate);
    }

    private async Task AutoAssignAsync(CancellationToken ct)
    {
        List<Job> pending;
        List<Driver> idle;
        using (var scope = scopes.CreateScope())
        {
            pending = (await scope.ServiceProvider.GetRequiredService<IJobRepository>().ListAsync(JobStatus.Pending, ct)).Reverse().ToList();
            idle = (await scope.ServiceProvider.GetRequiredService<IDriverRepository>().GetIdleAsync(ct)).ToList();
        }

        foreach (var job in pending)
        {
            if (idle.Count == 0) break;
            var nearest = idle.MinBy(d => GeoMath.DistanceMeters(d.CurrentLocation, job.Pickup))!;
            idle.Remove(nearest);
            await RunAsync(job.Id, s => s.AssignAsync(job.Id, nearest.Id, ct));
        }
    }

    /// <summary>Assigned drivers drive to the pickup point; on arrival the job goes InTransit.</summary>
    private async Task ApproachPickupAsync(SimulationOptions opts, CancellationToken ct)
    {
        List<Job> assigned;
        Dictionary<Guid, Driver> driversById;
        using (var scope = scopes.CreateScope())
        {
            assigned = (await scope.ServiceProvider.GetRequiredService<IJobRepository>().ListAsync(JobStatus.Assigned, ct)).ToList();
            driversById = (await scope.ServiceProvider.GetRequiredService<IDriverRepository>().ListAsync(ct)).ToDictionary(d => d.Id);
        }

        var now = time.GetUtcNow();
        var speed = opts.DriverSpeedMps * opts.TimeScale;
        var stepMeters = speed * opts.TickInterval.TotalSeconds;
        foreach (var job in assigned)
        {
            if (job.DriverId is not { } driverId || !driversById.TryGetValue(driverId, out var driver)) continue;

            var next = GeoMath.MoveToward(driver.CurrentLocation, job.Pickup, stepMeters);
            if (next == job.Pickup)
            {
                // Arrived; leave once at least PickupDwell has passed since assignment.
                if (now - (job.AssignedAt ?? job.UpdatedAt) >= opts.PickupDwell)
                    await RunAsync(job.Id, s => s.StartTransitAsync(job.Id, ct));
                else
                    await RunAsync(job.Id, s => s.MoveToPickupAsync(job.Id, next, 0, ct));
                continue;
            }

            var eta = GeoMath.EtaSeconds(GeoMath.DistanceMeters(next, job.Pickup), speed);
            await RunAsync(job.Id, s => s.MoveToPickupAsync(job.Id, next, eta, ct));
        }
    }

    private async Task MoveInTransitAsync(SimulationOptions opts, CancellationToken ct)
    {
        List<Job> moving;
        using (var scope = scopes.CreateScope())
            moving = (await scope.ServiceProvider.GetRequiredService<IJobRepository>().ListAsync(JobStatus.InTransit, ct)).ToList();

        var stepMeters = opts.DriverSpeedMps * opts.TickInterval.TotalSeconds * opts.TimeScale;
        foreach (var job in moving)
        {
            var next = GeoMath.MoveToward(job.CurrentLocation, job.Dropoff, stepMeters);
            if (next == job.Dropoff)
            {
                await RunAsync(job.Id, s => s.CompleteAsync(job.Id, ct));
                continue;
            }

            var remaining = GeoMath.DistanceMeters(next, job.Dropoff);
            var total = GeoMath.DistanceMeters(job.Pickup, job.Dropoff);
            var progress = total > 0 ? 1 - remaining / total : 1;
            var eta = GeoMath.EtaSeconds(remaining, opts.DriverSpeedMps * opts.TimeScale);
            await RunAsync(job.Id, s => s.UpdateProgressAsync(job.Id, next, eta, progress, ct));
        }
    }

    /// <summary>Runs one operation in a fresh scope; a failure on one job never stalls the rest of the batch.</summary>
    private async Task RunAsync(Guid jobId, Func<DispatchService, Task> action)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await action(scope.ServiceProvider.GetRequiredService<DispatchService>());
        }
        catch (Exception ex) when (ExpectedConflicts.Contains(ex.GetType()))
        {
            logger.LogDebug("Skipped job {JobId}: {Reason}", jobId, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Simulation step failed for job {JobId}", jobId);
        }
    }
}
