using LogisticsDispatch.Api.Contracts;
using LogisticsDispatch.Core.Routing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SimulationOptions = LogisticsDispatch.Infrastructure.Simulation.SimulationOptions;

namespace LogisticsDispatch.Api.Controllers;

/// <summary>
/// Orders. Everything here requires a signed-in user; what you may do depends on your role:
/// dispatchers run the board, customers see and cancel their own orders, drivers work the jobs assigned to them.
/// Someone else's order answers 404 (not 403) so ids can't be probed.
/// </summary>
[ApiController]
[Authorize]
[Route("api/jobs")]
public class JobsController(DispatchService dispatch, IJobRepository jobs, IOptions<SimulationOptions> sim) : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = $"{Roles.Customer},{Roles.Dispatcher}")]
    public async Task<ActionResult<JobDto>> Create(CreateJobRequest req, CancellationToken ct)
    {
        var isCustomer = User.IsInRole(Roles.Customer);
        // A customer's order is always in their own name; a dispatcher must say who it is for.
        var name = isCustomer ? User.DisplayName() : req.CustomerName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ModelState.AddModelError(nameof(req.CustomerName), "Customer name is required.");
            return ValidationProblem(ModelState);
        }

        var job = await dispatch.CreateJobAsync(name, req.Notes, req.Pickup!.ToLocation(), req.Dropoff!.ToLocation(),
            isCustomer ? User.UserId() : null, ct: ct);
        return CreatedAtAction(nameof(Get), new { id = job.Id }, job);
    }

    [HttpGet]
    public async Task<IEnumerable<JobDto>> List([FromQuery] JobStatus? status, CancellationToken ct)
    {
        IReadOnlyList<Job> list;
        if (User.IsDispatcher()) list = await jobs.ListAsync(status, ct);
        else if (User.IsInRole(Roles.Customer)) list = await jobs.ListForUserAsync(status, User.UserId(), null, ct);
        else if (User.DriverId() is { } driverId) list = await jobs.ListForUserAsync(status, null, driverId, ct);
        else list = [];
        return list.Select(j => JobDto.From(j));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<JobDto>> Get(Guid id, CancellationToken ct)
    {
        var job = await dispatch.GetJobDetailAsync(id, ct);
        return User.CanView(job.CustomerId, job.DriverId) ? job : NotFound();
    }

    /// <summary>Road paths for the job (driver→pickup and pickup→dropoff). A leg is null until the router has produced it.</summary>
    [HttpGet("{id:guid}/route")]
    public async Task<ActionResult<RouteDto>> GetRoute(Guid id, CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct);
        if (job is null || !User.CanView(job.CustomerId, job.DriverId)) return NotFound();
        return await dispatch.GetRouteAsync(id, ct);
    }

    [HttpPost("{id:guid}/assign")]
    [Authorize(Roles = Roles.Dispatcher)]
    public Task<JobDto> Assign(Guid id, AssignJobRequest req, CancellationToken ct) => dispatch.AssignAsync(id, req.DriverId!.Value, ct);

    /// <summary>Dispatcher manually offers a Pending job to a specific driver.</summary>
    [HttpPost("{id:guid}/offer")]
    [Authorize(Roles = Roles.Dispatcher)]
    public Task<OfferDto> Offer(Guid id, AssignJobRequest req, CancellationToken ct) =>
        dispatch.OfferJobAsync(id, req.DriverId!.Value, sim.Value.OfferTimeout, ct);

    /// <summary>Driver (or dispatcher) starts the trip: Assigned → InTransit.</summary>
    [HttpPost("{id:guid}/accept")]
    [Authorize(Roles = $"{Roles.Dispatcher},{Roles.Driver}")]
    public async Task<ActionResult<JobDto>> Accept(Guid id, CancellationToken ct) =>
        await Operable(id, ct) is { } denied ? denied : await dispatch.StartTransitAsync(id, ct);

    [HttpPost("{id:guid}/complete")]
    [Authorize(Roles = $"{Roles.Dispatcher},{Roles.Driver}")]
    public async Task<ActionResult<JobDto>> Complete(Guid id, CancellationToken ct) =>
        await Operable(id, ct) is { } denied ? denied : await dispatch.CompleteAsync(id, ct);

    /// <summary>Dispatchers can cancel anything; a customer can cancel their own order (the state machine decides if it's too late).</summary>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = $"{Roles.Dispatcher},{Roles.Customer}")]
    public async Task<ActionResult<JobDto>> Cancel(Guid id, CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct);
        if (job is null || !(User.IsDispatcher() || User.CanView(job.CustomerId, null))) return NotFound();
        return await dispatch.CancelAsync(id, User.IsDispatcher() ? "Cancelled by dispatcher" : "Cancelled by customer", ct);
    }

    /// <summary>null when the caller may move this job along; otherwise the response to return.</summary>
    private async Task<ActionResult?> Operable(Guid id, CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct);
        return job is null || !User.CanOperate(job.DriverId) ? NotFound() : null;
    }
}

[ApiController]
[Authorize(Roles = Roles.Dispatcher)]
[Route("api/drivers")]
public class DriversController(IDriverRepository drivers) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<DriverDto>> List(CancellationToken ct) =>
        (await drivers.ListAsync(ct)).Select(DriverDto.From);
}

[ApiController]
[Authorize(Roles = Roles.Dispatcher)]
[Route("api/status")]
public class StatusController(IJobRepository jobs, IDriverRepository drivers) : ControllerBase
{
    [HttpGet]
    public async Task<SystemStatusDto> Get(CancellationToken ct)
    {
        var allJobs = await jobs.ListAsync(ct: ct);
        var allDrivers = await drivers.ListAsync(ct);

        var byStatus = Enum.GetValues<JobStatus>().ToDictionary(s => s.ToString(), s => allJobs.Count(j => j.Status == s));
        var etas = allJobs.Where(j => j.Status == JobStatus.InTransit && j.EtaSeconds.HasValue).Select(j => (double)j.EtaSeconds!.Value).ToList();

        return new SystemStatusDto(
            byStatus,
            allDrivers.Count(d => d.Status == DriverStatus.Idle),
            allDrivers.Count(d => d.Status == DriverStatus.Busy),
            allDrivers.Count(d => d.Status == DriverStatus.Offline),
            etas.Count > 0 ? etas.Average() : null);
    }
}

[ApiController]
[Authorize(Roles = Roles.Dispatcher)]
[Route("api/stats")]
public class StatsController(IJobRepository jobs, IOfferRepository offers, IDriverRepository drivers, DispatchService dispatch, TimeProvider time) : ControllerBase
{
    /// <summary>Delivery stats for orders created in the last <c>hours</c> hours (1–168, default 24).</summary>
    [HttpGet]
    public async Task<StatsDto> Get([FromQuery] int hours = 24, CancellationToken ct = default)
    {
        hours = Math.Clamp(hours, 1, 168);
        return StatsCalculator.Compute(
            await jobs.ListAsync(ct: ct), await offers.ListAsync(ct: ct), await drivers.ListAsync(ct), time.GetUtcNow(), hours);
    }

    /// <summary>Demo: drops a burst of random orders into the system at once to see how the fleet copes.</summary>
    [HttpPost("rush")]
    public async Task<ActionResult<object>> Rush([FromQuery] int count = 10, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, RushHour.MaxOrders);
        foreach (var (pickup, dropoff) in RushHour.Generate(count, Random.Shared))
            await dispatch.CreateJobAsync("Rush order", null, pickup, dropoff, ct: ct);
        return new { created = count };
    }
}

[ApiController]
[Authorize(Roles = $"{Roles.Dispatcher},{Roles.Driver}")]
[Route("api/offers")]
public class OffersController(DispatchService dispatch, IOfferRepository offers) : ControllerBase
{
    /// <summary>Offers, newest first. Defaults to open (Pending) offers; pass status=all for history. Drivers only ever see their own.</summary>
    [HttpGet]
    public Task<IReadOnlyList<OfferDto>> List([FromQuery] string? status, [FromQuery] Guid? driverId, CancellationToken ct)
    {
        OfferStatus? parsed = string.Equals(status, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : Enum.TryParse<OfferStatus>(status, true, out var s) ? s : OfferStatus.Pending;

        if (User.IsInRole(Roles.Driver))
        {
            // never trust the query string for a driver: it's always "me"
            return User.DriverId() is { } me
                ? dispatch.ListOffersAsync(parsed, me, ct)
                : Task.FromResult<IReadOnlyList<OfferDto>>([]);
        }
        return dispatch.ListOffersAsync(parsed, driverId, ct);
    }

    /// <summary>A driver answers their own offers; a dispatcher can answer on a driver's behalf (e.g. by phone).</summary>
    [HttpPost("{id:guid}/accept")]
    public async Task<ActionResult<JobDto>> Accept(Guid id, CancellationToken ct) =>
        await Answerable(id, ct) is { } denied ? denied : await dispatch.AcceptOfferAsync(id, ct);

    [HttpPost("{id:guid}/decline")]
    public async Task<ActionResult<OfferDto>> Decline(Guid id, CancellationToken ct) =>
        await Answerable(id, ct) is { } denied ? denied : await dispatch.DeclineOfferAsync(id, ct);

    private async Task<ActionResult?> Answerable(Guid id, CancellationToken ct)
    {
        var offer = await offers.GetAsync(id, ct);
        if (offer is null) return NotFound();
        return User.IsDispatcher() || (User.DriverId() is { } me && me == offer.DriverId) ? null : NotFound();
    }
}

[ApiController]
[Authorize(Roles = Roles.Dispatcher)]
[Route("api/routing")]
public class RoutingController(IOptions<LogisticsDispatch.Infrastructure.Routing.RoutingOptions> options, IRouteProvider router) : ControllerBase
{
    // A short hop inside the covered area; if the engine is healthy this comes back as a real road path.
    private static readonly Location ProbeFrom = new(40.7128, -74.0060);
    private static readonly Location ProbeTo = new(40.7138, -74.0045);

    /// <summary>Which routing engine is configured and whether it is actually answering (probed with a tiny route).</summary>
    [HttpGet]
    public async Task<object> Get(CancellationToken ct)
    {
        var configured = !string.Equals(options.Value.Provider, "StraightLine", StringComparison.OrdinalIgnoreCase);
        if (!configured)
            return new { provider = options.Value.Provider, baseUrl = (string?)null, mode = "straight-line" };

        var probe = await router.GetRouteAsync(ProbeFrom, ProbeTo, ct);
        return new
        {
            provider = options.Value.Provider,
            baseUrl = options.Value.BaseUrl,
            mode = probe.IsStraightLine ? "straight-line (routing engine unreachable)" : "streets"
        };
    }
}

/// <summary>
/// Public tracking links: whoever holds the (unguessable) token can follow that one delivery, signed in or not.
/// An unknown or malformed token is simply a 404.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/track")]
public class TrackingController(DispatchService dispatch) : ControllerBase
{
    [HttpGet("{token}")]
    public async Task<ActionResult<TrackingDto>> Get(string token, CancellationToken ct) =>
        await dispatch.GetTrackingAsync(token, ct) is { } t ? t : NotFound();

    [HttpGet("{token}/route")]
    public async Task<ActionResult<RouteDto>> GetRoute(string token, CancellationToken ct) =>
        await dispatch.GetTrackingAsync(token, ct) is { } t ? await dispatch.GetRouteAsync(t.Id, ct) : NotFound();
}
