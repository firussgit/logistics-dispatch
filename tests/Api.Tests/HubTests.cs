using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace LogisticsDispatch.Api.Tests;

public class HubTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private HubConnection Connect() => new HubConnectionBuilder()
        .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/dispatch"), o =>
        {
            o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling; // TestServer has no WebSocket client by default
        })
        .Build();

    [Fact]
    public async Task Connected_client_receives_JobCreated_and_status_events()
    {
        var created = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var assigned = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var conn = Connect();
        conn.On<JsonElement>("JobCreated", e => created.TrySetResult(e));
        conn.On<JsonElement>("JobStatusChanged", e => assigned.TrySetResult(e));
        await conn.StartAsync();
        await conn.InvokeAsync("JoinDispatchGroup", "dispatchers");

        var client = factory.CreateClient();
        var job = await factory.CreateJobAsync(client, "Live");

        var createdEvt = await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(job.Id, createdEvt.GetProperty("id").GetGuid());
        Assert.Equal("Pending", createdEvt.GetProperty("status").GetString());

        var driver = (await factory.GetDriversAsync(client)).First();
        (await client.PostAsJsonAsync($"/api/jobs/{job.Id}/assign", new { driverId = driver.Id })).EnsureSuccessStatusCode();

        var statusEvt = await assigned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(job.Id, statusEvt.GetProperty("jobId").GetGuid());
        Assert.Equal("Assigned", statusEvt.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Client_can_drive_status_through_hub_and_illegal_transitions_surface_as_HubException()
    {
        await using var conn = Connect();
        await conn.StartAsync();

        var job = await factory.CreateJobAsync(factory.CreateClient(), "Hub-driven");

        // Pending -> Completed is illegal
        var ex = await Assert.ThrowsAsync<HubException>(() =>
            conn.InvokeAsync("SendStatusUpdate", new { jobId = job.Id, reference = job.Reference, status = "Completed", at = DateTimeOffset.UtcNow }));
        Assert.Contains("Cannot complete", ex.Message);
    }
}

