using System.Net;
using System.Net.Http.Json;
using LogisticsDispatch.Core.Enums;

namespace LogisticsDispatch.Api.Tests;

public class AssignConcurrencyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.DispatcherClient();

    [Fact]
    public async Task Same_job_assigned_to_two_drivers_concurrently_succeeds_exactly_once()
    {
        for (var round = 0; round < 5; round++)
        {
            var job = await factory.CreateJobAsync(_client);
            var idle = (await factory.GetDriversAsync(_client)).Where(d => d.Status == DriverStatus.Idle).Take(2).ToList();
            Assert.Equal(2, idle.Count);

            var gate = new TaskCompletionSource();
            var calls = idle.Select(async d =>
            {
                await gate.Task;
                return (await _client.PostAsJsonAsync($"/api/jobs/{job.Id}/assign", new { driverId = d.Id })).StatusCode;
            }).ToList();
            gate.SetResult();
            var codes = await Task.WhenAll(calls);

            Assert.Equal(1, codes.Count(c => c == HttpStatusCode.OK));
            Assert.Equal(1, codes.Count(c => c == HttpStatusCode.Conflict));

            // clean up so the next round has idle drivers again
            (await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Same_driver_assigned_to_two_jobs_concurrently_succeeds_exactly_once()
    {
        var jobA = await factory.CreateJobAsync(_client, "A");
        var jobB = await factory.CreateJobAsync(_client, "B");
        var driver = (await factory.GetDriversAsync(_client)).First(d => d.Status == DriverStatus.Idle);

        var gate = new TaskCompletionSource();
        var calls = new[] { jobA.Id, jobB.Id }.Select(async id =>
        {
            await gate.Task;
            return (await _client.PostAsJsonAsync($"/api/jobs/{id}/assign", new { driverId = driver.Id })).StatusCode;
        }).ToList();
        gate.SetResult();
        var codes = await Task.WhenAll(calls);

        Assert.Equal(1, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(1, codes.Count(c => c == HttpStatusCode.Conflict));
    }
}
