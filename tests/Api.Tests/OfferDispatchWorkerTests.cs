using System.Net.Http.Json;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Models;

namespace LogisticsDispatch.Api.Tests;

/// <summary>The worker offering jobs to drivers, with fast simulated responses. Simulated drivers always accept.</summary>
public class OfferDispatchWorkerTests : IClassFixture<OfferDispatchWorkerTests.AcceptingFactory>
{
    public class AcceptingFactory : ApiFactory
    {
        public AcceptingFactory() : base(simulation: true) { }
        protected override IEnumerable<(string, string)> ExtraSettings =>
        [
            ("Simulation:UseOffers", "true"),
            ("Simulation:SimulatedAcceptRate", "1"),
            ("Simulation:SimulatedResponseMin", "00:00:00"),
            ("Simulation:SimulatedResponseMax", "00:00:00.050"),
            ("Simulation:OfferTimeout", "00:00:00.700"),
            ("Simulation:DeclineCooldown", "00:00:10"),
        ];
    }

    private readonly AcceptingFactory _factory;
    private readonly HttpClient _client;

    public OfferDispatchWorkerTests(AcceptingFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Job_is_offered_accepted_and_delivered_and_the_unanswered_human_offer_times_out()
    {
        var created = await _factory.CreateJobAsync(_client, "Offered");

        JobDto? job = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            job = await _client.GetFromJsonAsync<JobDto>($"/api/jobs/{created.Id}", ApiFactory.Json);
            if (job!.Status == JobStatus.Completed) break;
            await Task.Delay(100);
        }

        Assert.Equal(JobStatus.Completed, job!.Status);

        var offers = (await _client.GetFromJsonAsync<List<OfferDto>>("/api/offers?status=all", ApiFactory.Json))!
            .Where(o => o.JobId == created.Id).OrderBy(o => o.CreatedAt).ToList();
        var drivers = await _factory.GetDriversAsync(_client);

        Assert.Single(offers, o => o.Status == OfferStatus.Accepted);
        Assert.Equal(job.DriverId, offers.Single(o => o.Status == OfferStatus.Accepted).DriverId);
        // The demo (human) driver is nearest to this pickup and never answers, so their offer must have expired first.
        var human = drivers.Single(d => !d.IsAutomated);
        Assert.Contains(offers, o => o.DriverId == human.Id && o.Status == OfferStatus.Expired);
        Assert.NotEqual(human.Id, job.DriverId);

        var events = _factory.Notifier.Events.ToList();
        Assert.Contains("offer:created", events);
        Assert.Contains("offer:Expired", events);
        Assert.Contains("offer:Accepted", events);
        Assert.True(events.IndexOf("offer:Expired") < events.IndexOf("offer:Accepted"));
    }
}

/// <summary>Every simulated driver declines: the offer must cascade through the fleet, each driver at most once.</summary>
public class OfferDeclineCascadeTests : IClassFixture<OfferDeclineCascadeTests.DecliningFactory>
{
    public class DecliningFactory : ApiFactory
    {
        public DecliningFactory() : base(simulation: true) { }
        protected override IEnumerable<(string, string)> ExtraSettings =>
        [
            ("Simulation:UseOffers", "true"),
            ("Simulation:SimulatedAcceptRate", "0"),
            ("Simulation:SimulatedResponseMin", "00:00:00"),
            ("Simulation:SimulatedResponseMax", "00:00:00.050"),
            ("Simulation:OfferTimeout", "00:00:00.700"),
            ("Simulation:DeclineCooldown", "00:01:00"),
        ];
    }

    private readonly DecliningFactory _factory;
    private readonly HttpClient _client;

    public OfferDeclineCascadeTests(DecliningFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Declined_offers_move_on_to_the_next_nearest_driver_without_repeating_anyone()
    {
        var created = await _factory.CreateJobAsync(_client, "Nobody wants it");
        var driverCount = (await _factory.GetDriversAsync(_client)).Count;

        List<OfferDto> offers = [];
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            offers = (await _client.GetFromJsonAsync<List<OfferDto>>("/api/offers?status=all", ApiFactory.Json))!
                .Where(o => o.JobId == created.Id).OrderBy(o => o.CreatedAt).ToList();
            if (offers.Count >= driverCount && offers.All(o => o.Status != OfferStatus.Pending)) break;
            await Task.Delay(100);
        }

        // every driver was tried exactly once (cooldown prevents repeats), none accepted, the job is still waiting
        Assert.Equal(driverCount, offers.Count);
        Assert.Equal(driverCount, offers.Select(o => o.DriverId).Distinct().Count());
        Assert.DoesNotContain(offers, o => o.Status == OfferStatus.Accepted);
        Assert.Equal(driverCount - 1, offers.Count(o => o.Status == OfferStatus.Declined)); // everyone but the human
        Assert.Equal(1, offers.Count(o => o.Status == OfferStatus.Expired));                 // the human never answered

        // and only one offer was ever open at a time: each started after the previous one closed
        for (var i = 1; i < offers.Count; i++)
            Assert.True(offers[i].CreatedAt >= offers[i - 1].CreatedAt);

        var job = await _client.GetFromJsonAsync<JobDto>($"/api/jobs/{created.Id}", ApiFactory.Json);
        Assert.Equal(JobStatus.Pending, job!.Status);
    }
}
