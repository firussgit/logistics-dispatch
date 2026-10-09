using System.Net.Http.Json;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Models;

namespace LogisticsDispatch.Api.Tests;

public class SimulationWorkerTests : IClassFixture<SimulationWorkerTests.FastSimFactory>
{
    public class FastSimFactory : ApiFactory { public FastSimFactory() : base(simulation: true) { } }

    private readonly FastSimFactory _factory;
    private readonly HttpClient _client;

    public SimulationWorkerTests(FastSimFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Job_is_assigned_moved_and_completed_without_any_client_action()
    {
        var created = await _factory.CreateJobAsync(_client, "Auto");

        JobDto? job = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            job = await _client.GetFromJsonAsync<JobDto>($"/api/jobs/{created.Id}", ApiFactory.Json);
            if (job!.Status == JobStatus.Completed) break;
            await Task.Delay(100);
        }

        Assert.Equal(JobStatus.Completed, job!.Status);
        Assert.Equal([JobStatus.Pending, JobStatus.Assigned, JobStatus.InTransit, JobStatus.Completed], job.History!.Select(h => h.To));
        Assert.Equal(1, job.Progress);

        var events = _factory.Notifier.Events.ToList();
        Assert.Contains("created", events);
        Assert.Contains("status:Assigned", events);
        Assert.Contains("status:InTransit", events);
        Assert.Contains("status:Completed", events);
        Assert.Contains("progress", events);
        Assert.True(events.IndexOf("status:Assigned") < events.IndexOf("status:Completed"));
    }

    [Fact]
    public async Task Driver_returns_to_idle_after_delivery()
    {
        var created = await _factory.CreateJobAsync(_client, "Idle check");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JobDto? job;
        do
        {
            await Task.Delay(100);
            job = await _client.GetFromJsonAsync<JobDto>($"/api/jobs/{created.Id}", ApiFactory.Json);
        } while (job!.Status != JobStatus.Completed && DateTime.UtcNow < deadline);

        Assert.Equal(JobStatus.Completed, job.Status);
        var driver = (await _factory.GetDriversAsync(_client)).Single(d => d.Id == job.DriverId);
        Assert.Equal(DriverStatus.Idle, driver.Status);
        Assert.Equal(job.Dropoff, driver.CurrentLocation);
    }
}
