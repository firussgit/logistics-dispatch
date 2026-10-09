using LogisticsDispatch.Api.Contracts;
using LogisticsDispatch.Core.Abstractions;
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

    [HttpPost("{id:guid}/assign")]
    public Task<JobDto> Assign(Guid id, AssignJobRequest req, CancellationToken ct) => dispatch.AssignAsync(id, req.DriverId!.Value, ct);

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
