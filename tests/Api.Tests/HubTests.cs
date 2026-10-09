using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Models;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace LogisticsDispatch.Api.Tests;

/// <summary>Who may connect to which hub, and who hears which events.</summary>
public class HubTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private HubConnection Connect(string hub, string? cookie = null) => new HubConnectionBuilder()
        .WithUrl(new Uri(factory.Server.BaseAddress, hub), o =>
        {
            o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling; // TestServer has no WebSocket client by default
            if (cookie is not null) o.Headers["Cookie"] = cookie;
        })
        .Build();

    /// <summary>Collects every event of the given names a connection receives.</summary>
    private static ConcurrentQueue<(string Name, JsonElement Payload)> Listen(HubConnection conn, params string[] events)
    {
        var received = new ConcurrentQueue<(string, JsonElement)>();
        foreach (var name in events) conn.On<JsonElement>(name, p => received.Enqueue((name, p)));
        return received;
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact]
    public async Task Anonymous_clients_cannot_connect_to_the_dispatch_hub()
    {
        await using var conn = Connect("hubs/dispatch");
        await Assert.ThrowsAnyAsync<Exception>(() => conn.StartAsync());
        Assert.NotEqual(HubConnectionState.Connected, conn.State);
    }

    [Fact]
    public async Task Dispatcher_hears_new_jobs_and_status_changes()
    {
        await using var conn = Connect("hubs/dispatch", await factory.LoginCookieAsync(Infrastructure.Data.DbSeeder.DemoDispatcherEmail));
        var events = Listen(conn, "JobCreated", "JobStatusChanged");
        await conn.StartAsync();

        var client = factory.DispatcherClient();
        var job = await factory.CreateJobAsync(client, "Live");
        Assert.True(await EventuallyAsync(() => events.Any(e => e.Name == "JobCreated" && e.Payload.GetProperty("id").GetGuid() == job.Id)));

        var driver = (await factory.GetDriversAsync(client)).First(d => d.Status == DriverStatus.Idle);
        (await client.PostAsJsonAsync($"/api/jobs/{job.Id}/assign", new { driverId = driver.Id })).EnsureSuccessStatusCode();

        Assert.True(await EventuallyAsync(() => events.Any(e => e.Name == "JobStatusChanged"
            && e.Payload.GetProperty("jobId").GetGuid() == job.Id && e.Payload.GetProperty("status").GetString() == "Assigned")));
    }

    [Fact]
    public async Task There_is_no_client_callable_way_to_join_a_group()
    {
        await using var conn = Connect("hubs/dispatch", await factory.LoginCookieAsync(Infrastructure.Data.DbSeeder.DemoCustomerEmail));
        await conn.StartAsync();

        // The old JoinDispatchGroup let any client eavesdrop on everything; it must be gone.
        await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync("JoinDispatchGroup", "dispatchers"));
    }

    [Fact]
    public async Task A_customer_only_hears_about_their_own_orders()
    {
        var (aClient, aMe) = await factory.RegisterCustomerAsync("Alice");
        var (bClient, bMe) = await factory.RegisterCustomerAsync("Bob");
        await using var aConn = Connect("hubs/dispatch", await factory.CustomerCookieAsync(aMe));
        await using var bConn = Connect("hubs/dispatch", await factory.CustomerCookieAsync(bMe));
        var aEvents = Listen(aConn, "JobCreated", "JobStatusChanged", "OfferCreated", "DriverUpdated");
        var bEvents = Listen(bConn, "JobCreated", "JobStatusChanged", "OfferCreated", "DriverUpdated");
        await aConn.StartAsync();
        await bConn.StartAsync();

        var alicesJob = await factory.CreateJobAsync(aClient);
        var dispatcher = factory.DispatcherClient();
        var driver = (await factory.GetDriversAsync(dispatcher)).First(d => d.Status == DriverStatus.Idle);
        (await dispatcher.PostAsJsonAsync($"/api/jobs/{alicesJob.Id}/assign", new { driverId = driver.Id })).EnsureSuccessStatusCode();

        Assert.True(await EventuallyAsync(() => aEvents.Any(e => e.Name == "JobStatusChanged" && e.Payload.GetProperty("jobId").GetGuid() == alicesJob.Id)));
        Assert.Contains(aEvents, e => e.Name == "JobCreated");
        await Task.Delay(500); // give any leak time to arrive
        Assert.Empty(bEvents);                                                    // Bob heard nothing about Alice's order
        Assert.DoesNotContain(aEvents, e => e.Name is "OfferCreated" or "DriverUpdated"); // and customers never get driver/offer traffic
    }

    [Fact]
    public async Task A_driver_only_hears_offers_made_to_them()
    {
        var dispatcher = factory.DispatcherClient();
        var drivers = await factory.GetDriversAsync(dispatcher);
        var human = drivers.Single(d => !d.IsAutomated);
        var other = drivers.First(d => d.IsAutomated && d.Status == DriverStatus.Idle);

        await using var conn = Connect("hubs/dispatch", await factory.LoginCookieAsync(Infrastructure.Data.DbSeeder.DemoDriverEmail));
        var events = Listen(conn, "OfferCreated", "OfferUpdated");
        await conn.StartAsync();

        var jobForOther = await factory.CreateJobAsync(dispatcher, "Not for you");
        (await dispatcher.PostAsJsonAsync($"/api/jobs/{jobForOther.Id}/offer", new { driverId = other.Id })).EnsureSuccessStatusCode();
        var jobForMe = await factory.CreateJobAsync(dispatcher, "For you");
        (await dispatcher.PostAsJsonAsync($"/api/jobs/{jobForMe.Id}/offer", new { driverId = human.Id })).EnsureSuccessStatusCode();

        Assert.True(await EventuallyAsync(() => events.Any(e => e.Name == "OfferCreated" && e.Payload.GetProperty("jobId").GetGuid() == jobForMe.Id)));
        await Task.Delay(300);
        Assert.DoesNotContain(events, e => e.Payload.GetProperty("jobId").GetGuid() == jobForOther.Id);
    }

    [Fact]
    public async Task Tracking_hub_is_anonymous_but_only_follows_the_job_its_token_unlocks()
    {
        var dispatcher = factory.DispatcherClient();
        var mine = await factory.CreateJobAsync(dispatcher, "Followed");
        var theirs = await factory.CreateJobAsync(dispatcher, "Someone else");
        var driver = (await factory.GetDriversAsync(dispatcher)).First(d => d.Status == DriverStatus.Idle);

        await using var conn = Connect("hubs/track"); // no cookie at all
        var events = Listen(conn, "JobStatusChanged");
        await conn.StartAsync();
        Assert.True(await conn.InvokeAsync<bool>("Track", mine.TrackingToken));
        Assert.False(await conn.InvokeAsync<bool>("Track", "not-a-real-token"));
        Assert.False(await conn.InvokeAsync<bool>("Track", new string('0', 32)));

        // activity on someone else's job is invisible; ours arrives
        var otherDriver = (await factory.GetDriversAsync(dispatcher)).First(d => d.Status == DriverStatus.Idle && d.Id != driver.Id);
        (await dispatcher.PostAsJsonAsync($"/api/jobs/{theirs.Id}/assign", new { driverId = otherDriver.Id })).EnsureSuccessStatusCode();
        (await dispatcher.PostAsJsonAsync($"/api/jobs/{mine.Id}/assign", new { driverId = driver.Id })).EnsureSuccessStatusCode();

        Assert.True(await EventuallyAsync(() => events.Any(e => e.Payload.GetProperty("jobId").GetGuid() == mine.Id)));
        Assert.DoesNotContain(events, e => e.Payload.GetProperty("jobId").GetGuid() == theirs.Id);
    }

    [Fact]
    public async Task Tracking_hub_does_not_expose_dispatch_traffic()
    {
        await using var conn = Connect("hubs/track");
        await conn.StartAsync();
        // methods of the authenticated hub simply don't exist here
        await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync("SendStatusUpdate", new { }));
    }

    [Fact]
    public async Task Status_updates_over_the_hub_follow_the_same_rules_as_the_rest_api()
    {
        var dispatcher = factory.DispatcherClient();
        var job = await factory.CreateJobAsync(dispatcher, "Hub-driven");
        object Update(JobStatus s) => new { jobId = job.Id, reference = job.Reference, status = s.ToString(), at = DateTimeOffset.UtcNow };

        // a customer may not drive jobs
        await using var customerConn = Connect("hubs/dispatch", await factory.LoginCookieAsync(Infrastructure.Data.DbSeeder.DemoCustomerEmail));
        await customerConn.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => customerConn.InvokeAsync("SendStatusUpdate", Update(JobStatus.Completed)));

        // a driver may not touch a job that isn't theirs
        await using var driverConn = Connect("hubs/dispatch", await factory.LoginCookieAsync(Infrastructure.Data.DbSeeder.DemoDriverEmail));
        await driverConn.StartAsync();
        var denied = await Assert.ThrowsAsync<HubException>(() => driverConn.InvokeAsync("SendStatusUpdate", Update(JobStatus.Completed)));
        Assert.Contains("not allowed", denied.Message);

        // a dispatcher is allowed, but the state machine still rejects Pending -> Completed
        await using var dispatcherConn = Connect("hubs/dispatch", await factory.LoginCookieAsync(Infrastructure.Data.DbSeeder.DemoDispatcherEmail));
        await dispatcherConn.StartAsync();
        var illegal = await Assert.ThrowsAsync<HubException>(() => dispatcherConn.InvokeAsync("SendStatusUpdate", Update(JobStatus.Completed)));
        Assert.Contains("Cannot complete", illegal.Message);
    }
}
