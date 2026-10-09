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
        [typeof(ConcurrencyConflictException), typeof(InvalidJobTransitionException), typeof(DriverUnavailableException), typeof(NotFoundException)];

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
        if (opts.AutoAssign) await AutoAssignAsync(ct);
        await StartAssignedAsync(opts, ct);
        await MoveInTransitAsync(opts, ct);
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

    private async Task StartAssignedAsync(SimulationOptions opts, CancellationToken ct)
    {
        List<Job> assigned;
        using (var scope = scopes.CreateScope())
            assigned = (await scope.ServiceProvider.GetRequiredService<IJobRepository>().ListAsync(JobStatus.Assigned, ct)).ToList();

        var now = time.GetUtcNow();
        foreach (var job in assigned.Where(j => now - (j.AssignedAt ?? j.UpdatedAt) >= opts.PickupDwell))
            await RunAsync(job.Id, s => s.StartTransitAsync(job.Id, ct));
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
