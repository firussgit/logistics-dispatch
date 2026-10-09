using LogisticsDispatch.Core.Abstractions;
using LogisticsDispatch.Core.Entities;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Exceptions;
using LogisticsDispatch.Core.Models;
using LogisticsDispatch.Core.Services;

namespace LogisticsDispatch.Core.Tests;

public class DispatchServiceTests
{
    private sealed class FakeJobs : IJobRepository
    {
        public readonly List<Job> Items = [];
        public void Add(Job job) => Items.Add(job);
        public Task<Job?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(j => j.Id == id));
        public Task<Job?> GetWithHistoryAsync(Guid id, CancellationToken ct = default) => GetAsync(id, ct);
        public Task<IReadOnlyList<Job>> ListAsync(JobStatus? status = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Job>>(Items.Where(j => status is null || j.Status == status).ToList());
    }

    private sealed class FakeDrivers : IDriverRepository
    {
        public readonly List<Driver> Items = [];
        public Task<Driver?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(d => d.Id == id));
        public Task<IReadOnlyList<Driver>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Driver>>(Items);
        public Task<IReadOnlyList<Driver>> GetIdleAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Driver>>(Items.Where(d => d.Status == DriverStatus.Idle).ToList());
    }

    private sealed class FakeUow : IUnitOfWork
    {
        public int Saves;
        public Task SaveChangesAsync(CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    private sealed class FakeNotifier : IDispatchNotifier
    {
        public readonly List<string> Events = [];
        public Task JobCreatedAsync(JobDto job, CancellationToken ct = default) { Events.Add("created"); return Task.CompletedTask; }
        public Task JobStatusChangedAsync(JobStatusChangedEvent e, CancellationToken ct = default) { Events.Add($"status:{e.Status}"); return Task.CompletedTask; }
        public Task JobProgressAsync(JobProgressEvent e, CancellationToken ct = default) { Events.Add("progress"); return Task.CompletedTask; }
        public Task DriverUpdatedAsync(DriverDto driver, CancellationToken ct = default) { Events.Add($"driver:{driver.Status}"); return Task.CompletedTask; }
    }

    private readonly FakeJobs _jobs = new();
    private readonly FakeDrivers _drivers = new();
    private readonly FakeUow _uow = new();
    private readonly FakeNotifier _notifier = new();
    private readonly DispatchService _svc;

    public DispatchServiceTests() =>
        _svc = new DispatchService(_jobs, _drivers, _uow, _notifier, TimeProvider.System);

    private Driver AddDriver(DriverStatus status = DriverStatus.Idle)
    {
        var d = new Driver { Name = "D", Status = status, CurrentLocation = new Location(40, -74) };
        _drivers.Items.Add(d);
        return d;
    }

    private Task<JobDto> NewJob() =>
        _svc.CreateJobAsync("Acme", null, new Location(40, -74), new Location(40.05, -74.05));

    [Fact]
    public async Task Create_saves_then_notifies()
    {
        await NewJob();
        Assert.Single(_jobs.Items);
        Assert.Equal(1, _uow.Saves);
        Assert.Equal(["created"], _notifier.Events);
    }

    [Fact]
    public async Task Assign_marks_driver_busy_and_notifies()
    {
        var driver = AddDriver();
        var job = await NewJob();

        var result = await _svc.AssignAsync(job.Id, driver.Id);

        Assert.Equal(JobStatus.Assigned, result.Status);
        Assert.Equal(DriverStatus.Busy, driver.Status);
        Assert.Equal(job.Id, driver.ActiveJobId);
        Assert.Contains("status:Assigned", _notifier.Events);
    }

    [Fact]
    public async Task Assign_rejects_busy_driver_and_leaves_job_pending()
    {
        var driver = AddDriver(DriverStatus.Busy);
        var job = await NewJob();
        var savesBefore = _uow.Saves;

        await Assert.ThrowsAsync<DriverUnavailableException>(() => _svc.AssignAsync(job.Id, driver.Id));

        Assert.Equal(JobStatus.Pending, _jobs.Items[0].Status);
        Assert.Equal(savesBefore, _uow.Saves);
        Assert.DoesNotContain("status:Assigned", _notifier.Events);
    }

    [Fact]
    public async Task Assign_unknown_ids_throw_not_found()
    {
        var driver = AddDriver();
        var job = await NewJob();
        await Assert.ThrowsAsync<NotFoundException>(() => _svc.AssignAsync(Guid.NewGuid(), driver.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _svc.AssignAsync(job.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task Complete_frees_driver_at_dropoff()
    {
        var driver = AddDriver();
        var job = await NewJob();
        await _svc.AssignAsync(job.Id, driver.Id);
        await _svc.StartTransitAsync(job.Id);

        await _svc.CompleteAsync(job.Id);

        Assert.Equal(DriverStatus.Idle, driver.Status);
        Assert.Null(driver.ActiveJobId);
        Assert.Equal(_jobs.Items[0].Dropoff, driver.CurrentLocation);
    }

    [Fact]
    public async Task Cancel_assigned_job_frees_driver()
    {
        var driver = AddDriver();
        var job = await NewJob();
        await _svc.AssignAsync(job.Id, driver.Id);

        await _svc.CancelAsync(job.Id, "no longer needed");

        Assert.Equal(DriverStatus.Idle, driver.Status);
        Assert.Equal(JobStatus.Cancelled, _jobs.Items[0].Status);
    }

    [Fact]
    public async Task Driver_can_approach_pickup_only_while_assigned()
    {
        var driver = AddDriver();
        var job = await NewJob();

        await Assert.ThrowsAsync<InvalidJobTransitionException>(() =>
            _svc.MoveToPickupAsync(job.Id, new Location(40.001, -74.001), 20));

        await _svc.AssignAsync(job.Id, driver.Id);
        await _svc.MoveToPickupAsync(job.Id, new Location(40.001, -74.001), 20);
        Assert.Equal(new Location(40.001, -74.001), driver.CurrentLocation);
        Assert.Equal(20, _jobs.Items[0].EtaSeconds);
        Assert.Equal(JobStatus.Assigned, _jobs.Items[0].Status);

        await _svc.StartTransitAsync(job.Id);
        await Assert.ThrowsAsync<InvalidJobTransitionException>(() =>
            _svc.MoveToPickupAsync(job.Id, new Location(40.002, -74.002), 10));
    }

    [Fact]
    public async Task Progress_only_allowed_while_in_transit()
    {
        var driver = AddDriver();
        var job = await NewJob();
        await _svc.AssignAsync(job.Id, driver.Id);

        await Assert.ThrowsAsync<InvalidJobTransitionException>(() =>
            _svc.UpdateProgressAsync(job.Id, new Location(40.01, -74.01), 30, 0.2));

        await _svc.StartTransitAsync(job.Id);
        await _svc.UpdateProgressAsync(job.Id, new Location(40.01, -74.01), 30, 0.2);
        Assert.Equal(0.2, _jobs.Items[0].Progress);
        Assert.Equal(new Location(40.01, -74.01), driver.CurrentLocation);
        Assert.Contains("progress", _notifier.Events);
    }
}
