using LogisticsDispatch.Core.Abstractions;
using LogisticsDispatch.Core.Entities;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Exceptions;
using LogisticsDispatch.Core.Models;
using LogisticsDispatch.Core.Routing;
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
        public Task<Job?> GetByTrackingTokenAsync(string token, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(j => j.TrackingToken == token));
        public Task<IReadOnlyList<Job>> ListForUserAsync(JobStatus? status, Guid? customerId, Guid? driverId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Job>>(Items.Where(j => (status is null || j.Status == status) && (customerId is null || j.CustomerId == customerId) && (driverId is null || j.DriverId == driverId)).ToList());
        public Task<IReadOnlyList<Job>> ListAsync(JobStatus? status = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Job>>(Items.Where(j => status is null || j.Status == status).ToList());
    }

    private sealed class FakeDrivers : IDriverRepository
    {
        public readonly List<Driver> Items = [];
        public void Add(Driver driver) => Items.Add(driver);
        public Task<Driver?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(d => d.Id == id));
        public Task<IReadOnlyList<Driver>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Driver>>(Items);
        public Task<IReadOnlyList<Driver>> GetIdleAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Driver>>(Items.Where(d => d.Status == DriverStatus.Idle).ToList());
    }

    private sealed class FakeOffers : IOfferRepository
    {
        public readonly List<JobOffer> Items = [];
        public void Add(JobOffer offer) => Items.Add(offer);
        public Task<JobOffer?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(o => o.Id == id));
        public Task<IReadOnlyList<JobOffer>> ListAsync(OfferStatus? status = null, Guid? driverId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JobOffer>>(Items.Where(o => (status is null || o.Status == status) && (driverId is null || o.DriverId == driverId)).ToList());
        public Task<IReadOnlyList<JobOffer>> ListForJobAsync(Guid jobId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JobOffer>>(Items.Where(o => o.JobId == jobId).ToList());
        public Task<IReadOnlyList<JobOffer>> ListSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JobOffer>>(Items.Where(o => o.CreatedAt >= since).ToList());
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeUow : IUnitOfWork
    {
        public int Saves;
        public Task SaveChangesAsync(CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    private sealed class FakeNotifier : IDispatchNotifier
    {
        public readonly List<string> Events = [];
        public readonly List<JobStatusChangedEvent> StatusEvents = [];
        public readonly List<JobProgressEvent> ProgressEvents = [];
        public Task JobCreatedAsync(JobDto job, CancellationToken ct = default) { Events.Add("created"); return Task.CompletedTask; }
        public Task JobStatusChangedAsync(JobStatusChangedEvent e, CancellationToken ct = default) { Events.Add($"status:{e.Status}"); StatusEvents.Add(e); return Task.CompletedTask; }
        public Task JobProgressAsync(JobProgressEvent e, CancellationToken ct = default) { Events.Add("progress"); ProgressEvents.Add(e); return Task.CompletedTask; }
        public Task DriverUpdatedAsync(DriverDto driver, CancellationToken ct = default) { Events.Add($"driver:{driver.Status}"); return Task.CompletedTask; }
        public Task OfferCreatedAsync(OfferDto offer, CancellationToken ct = default) { Events.Add("offer:created"); return Task.CompletedTask; }
        public Task OfferUpdatedAsync(OfferDto offer, CancellationToken ct = default) { Events.Add($"offer:{offer.Status}"); return Task.CompletedTask; }
        public Task RouteReadyAsync(RouteReadyEvent e, CancellationToken ct = default) { Events.Add($"route:{e.Kind}"); return Task.CompletedTask; }
    }

    private readonly FakeJobs _jobs = new();
    private readonly FakeDrivers _drivers = new();
    private readonly FakeOffers _offers = new();
    private readonly ManualTime _time = new();
    private readonly FakeUow _uow = new();
    private readonly FakeNotifier _notifier = new();
    private readonly DispatchService _svc;

    public DispatchServiceTests() =>
        _svc = new DispatchService(_jobs, _drivers, _offers, _uow, _notifier, _time);

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

    // ------------------------------------------------------------ offers

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Offer_creates_an_open_offer_and_notifies()
    {
        var driver = AddDriver();
        var job = await NewJob();

        var offer = await _svc.OfferJobAsync(job.Id, driver.Id, Ttl);

        Assert.Equal(OfferStatus.Pending, offer.Status);
        Assert.Equal(_time.Now + Ttl, offer.ExpiresAt);
        Assert.True(offer.Payout > 3m);
        Assert.Contains("offer:created", _notifier.Events);
        Assert.Equal(JobStatus.Pending, _jobs.Items[0].Status); // an offer alone doesn't change the job
        Assert.Equal(DriverStatus.Idle, driver.Status);
    }

    [Fact]
    public async Task Offer_rejects_busy_driver_non_pending_job_and_second_open_offer()
    {
        var idle = AddDriver();
        var busy = AddDriver(DriverStatus.Busy);
        var other = AddDriver();
        var job = await NewJob();

        await Assert.ThrowsAsync<DriverUnavailableException>(() => _svc.OfferJobAsync(job.Id, busy.Id, Ttl));

        await _svc.OfferJobAsync(job.Id, idle.Id, Ttl);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => _svc.OfferJobAsync(job.Id, other.Id, Ttl));

        await _svc.CancelAsync(job.Id);
        await Assert.ThrowsAsync<InvalidJobTransitionException>(() => _svc.OfferJobAsync(job.Id, other.Id, Ttl));
    }

    [Fact]
    public async Task Accept_assigns_job_marks_driver_busy_and_withdraws_competing_offers()
    {
        var a = AddDriver();
        var b = AddDriver();
        var job = await NewJob();
        var offerA = await _svc.OfferJobAsync(job.Id, a.Id, Ttl);
        // simulate a second, overlapping offer (e.g. created by a dispatcher after the first expired in another process)
        _offers.Items.Add(JobOffer.Create(job.Id, b.Id, _time.Now, Ttl));

        var result = await _svc.AcceptOfferAsync(offerA.Id);

        Assert.Equal(JobStatus.Assigned, result.Status);
        Assert.Equal(a.Id, result.DriverId);
        Assert.Equal(DriverStatus.Busy, a.Status);
        Assert.Equal(OfferStatus.Accepted, _offers.Items[0].Status);
        Assert.Equal(OfferStatus.Cancelled, _offers.Items[1].Status);
        Assert.Contains("offer:Accepted", _notifier.Events);
        Assert.Contains("offer:Cancelled", _notifier.Events);
    }

    [Fact]
    public async Task Accept_after_expiry_throws_and_changes_nothing()
    {
        var driver = AddDriver();
        var job = await NewJob();
        var offer = await _svc.OfferJobAsync(job.Id, driver.Id, Ttl);
        _time.Now += Ttl + TimeSpan.FromSeconds(1);

        await Assert.ThrowsAsync<OfferNotActiveException>(() => _svc.AcceptOfferAsync(offer.Id));

        Assert.Equal(JobStatus.Pending, _jobs.Items[0].Status);
        Assert.Equal(DriverStatus.Idle, driver.Status);
        Assert.Equal(OfferStatus.Pending, _offers.Items[0].Status); // the worker, not the failed accept, marks it expired
    }

    [Fact]
    public async Task Accept_fails_if_driver_became_busy_or_job_was_cancelled()
    {
        var driver = AddDriver();
        var job = await NewJob();
        var offer = await _svc.OfferJobAsync(job.Id, driver.Id, Ttl);

        driver.Status = DriverStatus.Busy;
        await Assert.ThrowsAsync<DriverUnavailableException>(() => _svc.AcceptOfferAsync(offer.Id));

        driver.Status = DriverStatus.Idle;
        await _svc.CancelAsync(job.Id);
        await Assert.ThrowsAsync<OfferNotActiveException>(() => _svc.AcceptOfferAsync(offer.Id)); // cancel withdrew it
    }

    [Fact]
    public async Task Decline_closes_the_offer_and_leaves_job_pending_and_driver_idle()
    {
        var driver = AddDriver();
        var job = await NewJob();
        var offer = await _svc.OfferJobAsync(job.Id, driver.Id, Ttl);

        var declined = await _svc.DeclineOfferAsync(offer.Id);

        Assert.Equal(OfferStatus.Declined, declined.Status);
        Assert.Equal(JobStatus.Pending, _jobs.Items[0].Status);
        Assert.Equal(DriverStatus.Idle, driver.Status);
        await Assert.ThrowsAsync<OfferNotActiveException>(() => _svc.DeclineOfferAsync(offer.Id)); // can't answer twice
    }

    [Fact]
    public async Task Expire_is_idempotent_and_ignores_answered_offers()
    {
        var driver = AddDriver();
        var job = await NewJob();
        var offer = await _svc.OfferJobAsync(job.Id, driver.Id, Ttl);

        await _svc.ExpireOfferAsync(offer.Id);
        await _svc.ExpireOfferAsync(offer.Id);
        Assert.Equal(OfferStatus.Expired, _offers.Items[0].Status);
        Assert.Single(_notifier.Events, e => e == "offer:Expired");

        var job2 = await NewJob();
        var offer2 = await _svc.OfferJobAsync(job2.Id, driver.Id, Ttl);
        await _svc.AcceptOfferAsync(offer2.Id);
        await _svc.ExpireOfferAsync(offer2.Id); // already accepted: no-op
        Assert.Equal(OfferStatus.Accepted, _offers.Items[1].Status);
    }

    [Fact]
    public async Task Manual_assign_and_cancel_withdraw_open_offers()
    {
        var offered = AddDriver();
        var manual = AddDriver();
        var job = await NewJob();
        await _svc.OfferJobAsync(job.Id, offered.Id, Ttl);

        await _svc.AssignAsync(job.Id, manual.Id);
        Assert.Equal(OfferStatus.Cancelled, _offers.Items[0].Status);

        var job2 = await NewJob();
        await _svc.OfferJobAsync(job2.Id, offered.Id, Ttl);
        await _svc.CancelAsync(job2.Id);
        Assert.Equal(OfferStatus.Cancelled, _offers.Items[1].Status);
    }

    // ------------------------------------------------------- ownership & tracking

    [Fact]
    public async Task Jobs_remember_their_customer_and_get_an_unguessable_tracking_token()
    {
        var customer = Guid.NewGuid();
        var mine = await _svc.CreateJobAsync("Pat", null, new Location(40, -74), new Location(40.05, -74.05), customer);
        var walkIn = await NewJob();

        Assert.Equal(customer, mine.CustomerId);
        Assert.Null(walkIn.CustomerId);
        Assert.Matches("^[0-9a-f]{32}$", mine.TrackingToken!);
        Assert.NotEqual(mine.TrackingToken, walkIn.TrackingToken);
    }

    [Fact]
    public async Task Tracking_resolves_only_a_valid_token_and_exposes_no_account_details()
    {
        var customer = Guid.NewGuid();
        var driver = AddDriver();
        var job = await _svc.CreateJobAsync("Pat Private", null, new Location(40, -74), new Location(40.05, -74.05), customer);
        await _svc.AssignAsync(job.Id, driver.Id);

        var tracking = await _svc.GetTrackingAsync(job.TrackingToken);

        Assert.NotNull(tracking);
        Assert.Equal(job.Id, tracking!.Id);
        Assert.Equal(JobStatus.Assigned, tracking.Status);
        Assert.Equal(driver.Name, tracking.DriverName);
        Assert.Equal(2, tracking.History.Count);
        // the DTO has no customer name/id/token members at all
        var members = typeof(TrackingDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("CustomerName", members);
        Assert.DoesNotContain("CustomerId", members);
        Assert.DoesNotContain("TrackingToken", members);

        Assert.Null(await _svc.GetTrackingAsync(null));
        Assert.Null(await _svc.GetTrackingAsync(""));
        Assert.Null(await _svc.GetTrackingAsync("short"));
        Assert.Null(await _svc.GetTrackingAsync(new string('a', 32)));
        Assert.Null(await _svc.GetTrackingAsync(new string('a', 33)));
        Assert.NotNull(await _svc.GetTrackingAsync(job.TrackingToken!.ToUpperInvariant())); // links survive being shouted
    }

    [Fact]
    public async Task Events_carry_the_customer_and_driver_so_they_can_be_routed_to_the_right_people()
    {
        var customer = Guid.NewGuid();
        var driver = AddDriver();
        var job = await _svc.CreateJobAsync("Pat", null, new Location(40, -74), new Location(40.05, -74.05), customer);

        await _svc.AssignAsync(job.Id, driver.Id);

        var e = _notifier.StatusEvents.Single();
        Assert.Equal(customer, e.CustomerId);
        Assert.Equal(driver.Id, e.DriverId);
        await _svc.StartTransitAsync(job.Id);
        await _svc.UpdateProgressAsync(job.Id, new Location(40.01, -74.01), 30, 0.3);
        var p = _notifier.ProgressEvents.Single();
        Assert.Equal(customer, p.CustomerId);
        Assert.Equal(driver.Id, p.DriverId);
    }

    // ------------------------------------------------------------ routes

    private static Route Path(params (double lat, double lng)[] pts) =>
        new(pts.Select(p => new Location(p.lat, p.lng)).ToList(), 0);

    [Fact]
    public async Task SetRoute_stores_the_trip_and_notifies_and_GetRoute_returns_it()
    {
        var job = await NewJob();

        await _svc.SetRouteAsync(job.Id, RouteKind.Trip, Path((40, -74), (40.02, -74), (40.05, -74.05)));

        Assert.Contains("route:Trip", _notifier.Events);
        var dto = await _svc.GetRouteAsync(job.Id);
        Assert.Null(dto.Approach);
        Assert.Equal(3, dto.Trip!.Count);
        Assert.Equal([40.02, -74], dto.Trip[1]);
        Assert.False(dto.IsStraightLine);
    }

    [Fact]
    public async Task Two_point_trip_route_is_reported_as_straight_line()
    {
        var job = await NewJob();
        await _svc.SetRouteAsync(job.Id, RouteKind.Trip, Route.StraightLine(new Location(40, -74), new Location(40.05, -74.05)));
        Assert.True((await _svc.GetRouteAsync(job.Id)).IsStraightLine);
    }

    [Fact]
    public async Task Approach_route_resets_travelled_distance_and_needs_an_assigned_job()
    {
        var driver = AddDriver();
        var job = await NewJob();
        var approach = Path((40.01, -74.01), (40.005, -74.005), (40, -74));

        await Assert.ThrowsAsync<InvalidJobTransitionException>(() => _svc.SetRouteAsync(job.Id, RouteKind.Approach, approach));

        await _svc.AssignAsync(job.Id, driver.Id);
        _jobs.Items[0].ApproachMeters = 999;
        await _svc.SetRouteAsync(job.Id, RouteKind.Approach, approach);

        Assert.Equal(0, _jobs.Items[0].ApproachMeters);
        Assert.Equal(3, (await _svc.GetRouteAsync(job.Id)).Approach!.Count);
    }

    [Fact]
    public async Task Routes_cannot_be_set_on_finished_jobs()
    {
        var job = await NewJob();
        await _svc.CancelAsync(job.Id);
        await Assert.ThrowsAsync<InvalidJobTransitionException>(() => _svc.SetRouteAsync(job.Id, RouteKind.Trip, Path((40, -74), (40.1, -74.1))));
    }

    [Fact]
    public async Task Offer_trip_distance_uses_the_road_route_once_known()
    {
        var driver = AddDriver();
        var job = await NewJob();
        var straight = (await _svc.OfferJobAsync(job.Id, driver.Id, Ttl)).TripMeters;
        await _svc.DeclineOfferAsync(_offers.Items[0].Id);

        // a long detour (via lat 40.2) makes the road trip far longer than the crow-flies distance
        await _svc.SetRouteAsync(job.Id, RouteKind.Trip, Path((40, -74), (40.2, -74), (40.05, -74.05)));
        var viaRoad = (await _svc.OfferJobAsync(job.Id, driver.Id, Ttl)).TripMeters;

        Assert.True(viaRoad > straight * 3, $"expected road trip ({viaRoad:0} m) to dwarf straight line ({straight:0} m)");
    }

    [Fact]
    public async Task Driver_can_approach_pickup_only_while_assigned()
    {
        var driver = AddDriver();
        var job = await NewJob();

        await Assert.ThrowsAsync<InvalidJobTransitionException>(() =>
            _svc.MoveToPickupAsync(job.Id, new Location(40.001, -74.001), 20, 150));

        await _svc.AssignAsync(job.Id, driver.Id);
        await _svc.MoveToPickupAsync(job.Id, new Location(40.001, -74.001), 20, 150);
        Assert.Equal(new Location(40.001, -74.001), driver.CurrentLocation);
        Assert.Equal(20, _jobs.Items[0].EtaSeconds);
        Assert.Equal(JobStatus.Assigned, _jobs.Items[0].Status);

        await _svc.StartTransitAsync(job.Id);
        await Assert.ThrowsAsync<InvalidJobTransitionException>(() =>
            _svc.MoveToPickupAsync(job.Id, new Location(40.002, -74.002), 10, 300));
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
