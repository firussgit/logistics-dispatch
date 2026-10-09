using LogisticsDispatch.Api.Contracts;
using LogisticsDispatch.Core.Abstractions;
using LogisticsDispatch.Core.Entities;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Models;
using LogisticsDispatch.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogisticsDispatch.Api.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController(DispatchService dispatch, IJobRepository jobs) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<JobDto>> Create(CreateJobRequest req, CancellationToken ct)
    {
        var job = await dispatch.CreateJobAsync(req.CustomerName.Trim(), req.Notes, req.Pickup!.ToLocation(), req.Dropoff!.ToLocation(), ct);
        return CreatedAtAction(nameof(Get), new { id = job.Id }, job);
    }

    [HttpGet]
    public async Task<IEnumerable<JobDto>> List([FromQuery] JobStatus? status, CancellationToken ct) =>
        (await jobs.ListAsync(status, ct)).Select(j => JobDto.From(j));

    [HttpGet("{id:guid}")]
    public Task<JobDto> Get(Guid id, CancellationToken ct) => dispatch.GetJobDetailAsync(id, ct);

    /// <summary>Road paths for the job (driver→pickup and pickup→dropoff). A leg is null until the router has produced it.</summary>
    [HttpGet("{id:guid}/route")]
    public Task<RouteDto> GetRoute(Guid id, CancellationToken ct) => dispatch.GetRouteAsync(id, ct);

    [HttpPost("{id:guid}/assign")]
    public Task<JobDto> Assign(Guid id, AssignJobRequest req, CancellationToken ct) => dispatch.AssignAsync(id, req.DriverId!.Value, ct);

    /// <summary>Dispatcher manually offers a Pending job to a specific driver.</summary>
    [HttpPost("{id:guid}/offer")]
    public Task<OfferDto> Offer(Guid id, AssignJobRequest req, [FromServices] Microsoft.Extensions.Options.IOptions<LogisticsDispatch.Infrastructure.Simulation.SimulationOptions> sim, CancellationToken ct) =>
        dispatch.OfferJobAsync(id, req.DriverId!.Value, sim.Value.OfferTimeout, ct);

    [HttpPost("{id:guid}/accept")]
    public Task<JobDto> Accept(Guid id, CancellationToken ct) => dispatch.StartTransitAsync(id, ct);

    [HttpPost("{id:guid}/complete")]
    public Task<JobDto> Complete(Guid id, CancellationToken ct) => dispatch.CompleteAsync(id, ct);

    [HttpPost("{id:guid}/cancel")]
    public Task<JobDto> Cancel(Guid id, CancellationToken ct) => dispatch.CancelAsync(id, "Cancelled via API", ct);
}

[ApiController]
[Route("api/drivers")]
public class DriversController(IDriverRepository drivers) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<DriverDto>> List(CancellationToken ct) =>
        (await drivers.ListAsync(ct)).Select(DriverDto.From);
}

[ApiController]
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
[Route("api/offers")]
public class OffersController(DispatchService dispatch) : ControllerBase
{
    /// <summary>Offers, newest first. Defaults to open (Pending) offers; pass status=all for history.</summary>
    [HttpGet]
    public Task<IReadOnlyList<OfferDto>> List([FromQuery] string? status, [FromQuery] Guid? driverId, CancellationToken ct)
    {
        OfferStatus? parsed = string.Equals(status, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : Enum.TryParse<OfferStatus>(status, true, out var s) ? s : OfferStatus.Pending;
        return dispatch.ListOffersAsync(parsed, driverId, ct);
    }

    [HttpPost("{id:guid}/accept")]
    public Task<JobDto> Accept(Guid id, CancellationToken ct) => dispatch.AcceptOfferAsync(id, ct);

    [HttpPost("{id:guid}/decline")]
    public Task<OfferDto> Decline(Guid id, CancellationToken ct) => dispatch.DeclineOfferAsync(id, ct);
}

[ApiController]
[Route("api/routing")]
public class RoutingController(
    Microsoft.Extensions.Options.IOptions<LogisticsDispatch.Infrastructure.Routing.RoutingOptions> options,
    LogisticsDispatch.Core.Routing.IRouteProvider router) : ControllerBase
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
