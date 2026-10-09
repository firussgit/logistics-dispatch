using System.Net;
using System.Net.Http.Json;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Models;

namespace LogisticsDispatch.Api.Tests;

public class JobsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Create_returns_201_with_location_header_and_pending_status()
    {
        var res = await _client.PostAsJsonAsync("/api/jobs", ApiFactory.NewJobBody());

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var job = await res.Content.ReadFromJsonAsync<JobDto>(ApiFactory.Json);
        Assert.Equal(JobStatus.Pending, job!.Status);
        Assert.StartsWith("JOB-", job.Reference);
        Assert.EndsWith($"/api/jobs/{job.Id}", res.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("""{"customerName":"","pickup":{"lat":1,"lng":1},"dropoff":{"lat":2,"lng":2}}""")]
    [InlineData("""{"customerName":"A","pickup":{"lat":91,"lng":1},"dropoff":{"lat":2,"lng":2}}""")]
    [InlineData("""{"customerName":"A","pickup":{"lat":1,"lng":181},"dropoff":{"lat":2,"lng":2}}""")]
    [InlineData("""{"customerName":"A","pickup":{"lat":1,"lng":1},"dropoff":{"lat":1,"lng":1}}""")]
    [InlineData("""{"customerName":"A","pickup":{"lat":1,"lng":1}}""")]
    public async Task Create_rejects_invalid_input_with_400(string json)
    {
        var res = await _client.PostAsync("/api/jobs", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Get_unknown_job_returns_404_problem_details()
    {
        var res = await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Detail_includes_status_history()
    {
        var created = await factory.CreateJobAsync(_client);
        var driver = (await factory.GetDriversAsync(_client)).First(d => d.Status == DriverStatus.Idle);
        (await _client.PostAsJsonAsync($"/api/jobs/{created.Id}/assign", new { driverId = driver.Id })).EnsureSuccessStatusCode();

        var detail = await _client.GetFromJsonAsync<JobDto>($"/api/jobs/{created.Id}", ApiFactory.Json);

        Assert.Equal(JobStatus.Assigned, detail!.Status);
        Assert.Equal([JobStatus.Pending, JobStatus.Assigned], detail.History!.Select(h => h.To));
    }

    [Fact]
    public async Task Full_manual_lifecycle_and_driver_is_freed()
    {
        var job = await factory.CreateJobAsync(_client);
        var driver = (await factory.GetDriversAsync(_client)).First(d => d.Status == DriverStatus.Idle);

        (await _client.PostAsJsonAsync($"/api/jobs/{job.Id}/assign", new { driverId = driver.Id })).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/jobs/{job.Id}/accept", null)).EnsureSuccessStatusCode();
        var done = await _client.PostAsync($"/api/jobs/{job.Id}/complete", null);
        done.EnsureSuccessStatusCode();

        Assert.Equal(JobStatus.Completed, (await done.Content.ReadFromJsonAsync<JobDto>(ApiFactory.Json))!.Status);
        Assert.Equal(DriverStatus.Idle, (await factory.GetDriversAsync(_client)).Single(d => d.Id == driver.Id).Status);
    }

    [Fact]
    public async Task Illegal_transition_returns_409()
    {
        var job = await factory.CreateJobAsync(_client);
        var res = await _client.PostAsync($"/api/jobs/{job.Id}/complete", null); // Pending -> Completed is illegal
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Cancel_pending_job_succeeds_then_second_cancel_conflicts()
    {
        var job = await factory.CreateJobAsync(_client);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Assign_with_unknown_driver_returns_404_and_missing_body_400()
    {
        var job = await factory.CreateJobAsync(_client);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsJsonAsync($"/api/jobs/{job.Id}/assign", new { driverId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync($"/api/jobs/{job.Id}/assign", new { })).StatusCode);
    }

    [Fact]
    public async Task List_filters_by_status_and_status_endpoint_counts_match()
    {
        var job = await factory.CreateJobAsync(_client, "Filter me");
        var pending = await _client.GetFromJsonAsync<List<JobDto>>("/api/jobs?status=Pending", ApiFactory.Json);
        Assert.Contains(pending!, j => j.Id == job.Id);
        Assert.All(pending!, j => Assert.Equal(JobStatus.Pending, j.Status));

        var status = await _client.GetFromJsonAsync<SystemStatusDto>("/api/status", ApiFactory.Json);
        Assert.Equal(pending!.Count, status!.JobsByStatus["Pending"]);
        Assert.Equal((await factory.GetDriversAsync(_client)).Count, status.IdleDrivers + status.BusyDrivers + status.OfflineDrivers);
    }
}
