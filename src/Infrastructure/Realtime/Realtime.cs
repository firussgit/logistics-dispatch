using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace LogisticsDispatch.Infrastructure.Realtime;

public static class HubEvents
{
    public const string JobCreated = "JobCreated";
    public const string JobStatusChanged = "JobStatusChanged";
    public const string JobProgress = "JobProgress";
    public const string DriverUpdated = "DriverUpdated";
    public const string OfferCreated = "OfferCreated";
    public const string OfferUpdated = "OfferUpdated";
    public const string RouteReady = "RouteReady";
}

/// <summary>
/// Who hears what. Groups are only ever joined by the server (from the signed-in user's role, or a valid tracking token) —
/// there is deliberately no client-callable "join group".
/// </summary>
public static class HubGroups
{
    public const string Dispatchers = "dispatchers";
    public static string Driver(Guid driverId) => $"driver-{driverId:N}";
    public static string Customer(Guid userId) => $"customer-{userId:N}";
    public static string Tracking(Guid jobId) => $"job-{jobId:N}";
}

/// <summary>Authenticated hub for the dispatcher console, driver app and customer page.</summary>
[Authorize]
public class DispatchHub(DispatchService dispatch, IJobRepository jobs) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var user = Context.User!;
        if (user.IsDispatcher())
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Dispatchers);
        if (user.IsInRole(Roles.Driver) && user.DriverId() is { } driverId)
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Driver(driverId));
        if (user.IsInRole(Roles.Customer) && user.UserId() is { } userId)
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Customer(userId));
        await base.OnConnectedAsync();
    }

    /// <summary>Client-initiated status change (e.g. a driver app). Same rules as the REST API: dispatcher, or the job's own driver.</summary>
    public async Task SendStatusUpdate(JobStatusChangedEvent update)
    {
        try
        {
            var job = await jobs.GetAsync(update.JobId, Context.ConnectionAborted)
                      ?? throw new NotFoundException("Job", update.JobId);
            if (!Context.User!.CanOperate(job.DriverId))
                throw new HubException("You are not allowed to change this job.");

            switch (update.Status)
            {
                case JobStatus.InTransit: await dispatch.StartTransitAsync(update.JobId, Context.ConnectionAborted); break;
                case JobStatus.Completed: await dispatch.CompleteAsync(update.JobId, Context.ConnectionAborted); break;
                case JobStatus.Cancelled when Context.User!.IsDispatcher():
                    await dispatch.CancelAsync(update.JobId, "Cancelled via hub", Context.ConnectionAborted); break;
                default: throw new HubException($"Status {update.Status} cannot be set by this client.");
            }
        }
        catch (Exception ex) when (ex is NotFoundException or InvalidJobTransitionException or ConcurrencyConflictException or DriverUnavailableException)
        {
            throw new HubException(ex.Message);
        }
    }
}

/// <summary>Anonymous hub for customer tracking links. A connection hears about exactly one job: the one its token unlocks.</summary>
[AllowAnonymous]
public class TrackingHub(DispatchService dispatch) : Hub
{
    /// <returns>true if the token is valid and the connection now follows that delivery.</returns>
    public async Task<bool> Track(string token)
    {
        var tracking = await dispatch.GetTrackingAsync(token, Context.ConnectionAborted);
        if (tracking is null) return false;
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Tracking(tracking.Id));
        return true;
    }
}

public class SignalRDispatchNotifier(
    IHubContext<DispatchHub> hub,
    IHubContext<TrackingHub> trackingHub,
    ILogger<SignalRDispatchNotifier> logger) : IDispatchNotifier
{
    private static List<string> JobAudience(Guid? customerId, Guid? driverId)
    {
        var groups = new List<string> { HubGroups.Dispatchers };
        if (customerId is { } c) groups.Add(HubGroups.Customer(c));
        if (driverId is { } d) groups.Add(HubGroups.Driver(d));
        return groups;
    }

    public Task JobCreatedAsync(JobDto job, CancellationToken ct = default) =>
        SendAsync(HubEvents.JobCreated, job, JobAudience(job.CustomerId, null), null, ct);

    public Task JobStatusChangedAsync(JobStatusChangedEvent e, CancellationToken ct = default)
    {
        logger.LogInformation("Job {Reference} ({JobId}) is now {Status} (driver {DriverId})", e.Reference, e.JobId, e.Status, e.DriverId);
        return SendAsync(HubEvents.JobStatusChanged, e, JobAudience(e.CustomerId, e.DriverId), HubGroups.Tracking(e.JobId), ct);
    }

    public Task JobProgressAsync(JobProgressEvent e, CancellationToken ct = default) =>
        SendAsync(HubEvents.JobProgress, e, JobAudience(e.CustomerId, e.DriverId), HubGroups.Tracking(e.JobId), ct);

    public Task RouteReadyAsync(RouteReadyEvent e, CancellationToken ct = default) =>
        SendAsync(HubEvents.RouteReady, e, JobAudience(e.CustomerId, e.DriverId), HubGroups.Tracking(e.JobId), ct);

    public Task DriverUpdatedAsync(DriverDto driver, CancellationToken ct = default) =>
        SendAsync(HubEvents.DriverUpdated, driver, [HubGroups.Dispatchers, HubGroups.Driver(driver.Id)], null, ct);

    public Task OfferCreatedAsync(OfferDto offer, CancellationToken ct = default) =>
        SendAsync(HubEvents.OfferCreated, offer, [HubGroups.Dispatchers, HubGroups.Driver(offer.DriverId)], null, ct);

    public Task OfferUpdatedAsync(OfferDto offer, CancellationToken ct = default) =>
        SendAsync(HubEvents.OfferUpdated, offer, [HubGroups.Dispatchers, HubGroups.Driver(offer.DriverId)], null, ct);

    private async Task SendAsync(string method, object payload, IReadOnlyList<string> groups, string? trackingGroup, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Groups(groups).SendAsync(method, payload, ct);
            if (trackingGroup is not null)
                await trackingHub.Clients.Group(trackingGroup).SendAsync(method, payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Data is already committed; a failed push must never fail the request.
            logger.LogWarning(ex, "Failed to broadcast {Method}", method);
        }
    }
}
