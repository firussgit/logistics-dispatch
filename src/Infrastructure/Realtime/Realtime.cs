using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace LogisticsDispatch.Infrastructure.Realtime;

public static class HubEvents
{
    public const string JobCreated = "JobCreated";
    public const string JobStatusChanged = "JobStatusChanged";
    public const string JobProgress = "JobProgress";
    public const string DriverUpdated = "DriverUpdated";
    public const string DispatchGroup = "dispatchers";
}

public class DispatchHub(DispatchService dispatch) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, HubEvents.DispatchGroup);
        await base.OnConnectedAsync();
    }

    public Task JoinDispatchGroup(string group) =>
        Groups.AddToGroupAsync(Context.ConnectionId, group);

    public Task LeaveDispatchGroup(string group) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, group);

    /// <summary>Client-initiated status change (e.g. a driver app). Routed through the same state machine as the REST API.</summary>
    public async Task SendStatusUpdate(JobStatusChangedEvent update)
    {
        try
        {
            switch (update.Status)
            {
                case JobStatus.InTransit: await dispatch.StartTransitAsync(update.JobId, Context.ConnectionAborted); break;
                case JobStatus.Completed: await dispatch.CompleteAsync(update.JobId, Context.ConnectionAborted); break;
                case JobStatus.Cancelled: await dispatch.CancelAsync(update.JobId, "Cancelled via hub", Context.ConnectionAborted); break;
                default: throw new HubException($"Status {update.Status} cannot be set by a client.");
            }
        }
        catch (Exception ex) when (ex is NotFoundException or InvalidJobTransitionException or ConcurrencyConflictException or DriverUnavailableException)
        {
            throw new HubException(ex.Message);
        }
    }
}

public class SignalRDispatchNotifier(IHubContext<DispatchHub> hub, ILogger<SignalRDispatchNotifier> logger) : IDispatchNotifier
{
    public Task JobCreatedAsync(JobDto job, CancellationToken ct = default) => SendAsync(HubEvents.JobCreated, job, ct);
    public Task JobStatusChangedAsync(JobStatusChangedEvent e, CancellationToken ct = default)
    {
        logger.LogInformation("Job {Reference} ({JobId}) is now {Status} (driver {DriverId})", e.Reference, e.JobId, e.Status, e.DriverId);
        return SendAsync(HubEvents.JobStatusChanged, e, ct);
    }
    public Task JobProgressAsync(JobProgressEvent e, CancellationToken ct = default) => SendAsync(HubEvents.JobProgress, e, ct);
    public Task DriverUpdatedAsync(DriverDto driver, CancellationToken ct = default) => SendAsync(HubEvents.DriverUpdated, driver, ct);

    private async Task SendAsync(string method, object payload, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Group(HubEvents.DispatchGroup).SendAsync(method, payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Data is already committed; a failed push must never fail the request.
            logger.LogWarning(ex, "Failed to broadcast {Method}", method);
        }
    }
}
