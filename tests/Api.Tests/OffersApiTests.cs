using System.Net;
using System.Net.Http.Json;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Models;

namespace LogisticsDispatch.Api.Tests;

/// <summary>Manual offer flow over REST (simulation off, so nothing answers on its own).</summary>
public class OffersApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.DispatcherClient();

    private async Task<(JobDto Job, DriverDto Driver, OfferDto Offer)> OfferNewJobAsync()
    {
        var job = await factory.CreateJobAsync(_client);
        var driver = (await factory.GetDriversAsync(_client)).First(d => d.Status == DriverStatus.Idle && d.IsAutomated);
        var res = await _client.PostAsJsonAsync($"/api/jobs/{job.Id}/offer", new { driverId = driver.Id });
        res.EnsureSuccessStatusCode();
        return (job, driver, (await res.Content.ReadFromJsonAsync<OfferDto>(ApiFactory.Json))!);
    }

    private Task<List<OfferDto>> GetOffersAsync(string query) =>
        _client.GetFromJsonAsync<List<OfferDto>>($"/api/offers?{query}", ApiFactory.Json)!;

    [Fact]
    public async Task Offer_is_listed_for_the_driver_with_job_context()
    {
        var (job, driver, offer) = await OfferNewJobAsync();

        var open = await GetOffersAsync($"driverId={driver.Id}");

        var listed = Assert.Single(open, o => o.Id == offer.Id);
        Assert.Equal(OfferStatus.Pending, listed.Status);
        Assert.Equal(job.Reference, listed.Reference);
        Assert.True(listed.ExpiresAt > listed.CreatedAt);
        Assert.True(listed.TripMeters > 1000);
        Assert.True(listed.Payout > 3m);
    }

    [Fact]
    public async Task Accepting_assigns_the_job_and_closes_the_offer()
    {
        var (job, driver, offer) = await OfferNewJobAsync();

        var res = await _client.PostAsync($"/api/offers/{offer.Id}/accept", null);

        res.EnsureSuccessStatusCode();
        var assigned = await res.Content.ReadFromJsonAsync<JobDto>(ApiFactory.Json);
        Assert.Equal(JobStatus.Assigned, assigned!.Status);
        Assert.Equal(driver.Id, assigned.DriverId);
        Assert.Equal(DriverStatus.Busy, (await factory.GetDriversAsync(_client)).Single(d => d.Id == driver.Id).Status);
        Assert.DoesNotContain(await GetOffersAsync("status=pending"), o => o.Id == offer.Id);

        // can't be answered a second time
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/offers/{offer.Id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/offers/{offer.Id}/decline", null)).StatusCode);
    }

    [Fact]
    public async Task Declining_keeps_the_job_pending_and_the_driver_free()
    {
        var (job, driver, offer) = await OfferNewJobAsync();

        var res = await _client.PostAsync($"/api/offers/{offer.Id}/decline", null);

        res.EnsureSuccessStatusCode();
        Assert.Equal(OfferStatus.Declined, (await res.Content.ReadFromJsonAsync<OfferDto>(ApiFactory.Json))!.Status);
        Assert.Equal(JobStatus.Pending, (await _client.GetFromJsonAsync<JobDto>($"/api/jobs/{job.Id}", ApiFactory.Json))!.Status);
        Assert.Equal(DriverStatus.Idle, (await factory.GetDriversAsync(_client)).Single(d => d.Id == driver.Id).Status);
        Assert.Contains(await GetOffersAsync("status=all"), o => o.Id == offer.Id && o.Status == OfferStatus.Declined);
    }

    [Fact]
    public async Task A_job_can_have_only_one_open_offer_at_a_time()
    {
        var (job, _, _) = await OfferNewJobAsync();
        var other = (await factory.GetDriversAsync(_client)).First(d => d.Status == DriverStatus.Idle && d.Id != job.DriverId);

        var res = await _client.PostAsJsonAsync($"/api/jobs/{job.Id}/offer", new { driverId = other.Id });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Cancelling_the_job_withdraws_its_open_offer()
    {
        var (job, _, offer) = await OfferNewJobAsync();

        (await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/offers/{offer.Id}/accept", null)).StatusCode);
        Assert.Contains(await GetOffersAsync("status=all"), o => o.Id == offer.Id && o.Status == OfferStatus.Cancelled);
    }

    [Fact]
    public async Task Offering_to_unknown_job_or_driver_returns_404()
    {
        var job = await factory.CreateJobAsync(_client);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/jobs/{job.Id}/offer", new { driverId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/offers/{Guid.NewGuid()}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task Two_simultaneous_accepts_of_competing_offers_for_one_driver_succeed_exactly_once()
    {
        var driver = (await factory.GetDriversAsync(_client)).First(d => d.Status == DriverStatus.Idle && d.IsAutomated);
        var jobA = await factory.CreateJobAsync(_client, "A");
        var jobB = await factory.CreateJobAsync(_client, "B");
        var offerA = await (await _client.PostAsJsonAsync($"/api/jobs/{jobA.Id}/offer", new { driverId = driver.Id })).Content.ReadFromJsonAsync<OfferDto>(ApiFactory.Json);
        var offerB = await (await _client.PostAsJsonAsync($"/api/jobs/{jobB.Id}/offer", new { driverId = driver.Id })).Content.ReadFromJsonAsync<OfferDto>(ApiFactory.Json);

        var gate = new TaskCompletionSource();
        var calls = new[] { offerA!.Id, offerB!.Id }.Select(async id =>
        {
            await gate.Task;
            return (await _client.PostAsync($"/api/offers/{id}/accept", null)).StatusCode;
        }).ToList();
        gate.SetResult();
        var codes = await Task.WhenAll(calls);

        Assert.Equal(1, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(1, codes.Count(c => c == HttpStatusCode.Conflict));
    }
}
