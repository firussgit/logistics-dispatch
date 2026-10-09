using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogisticsDispatch.Core.Abstractions;
using LogisticsDispatch.Core.Models;
using LogisticsDispatch.Infrastructure.Realtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogisticsDispatch.Api.Tests;

/// <summary>Decorates the real SignalR notifier and records everything that was published.</summary>
public sealed class RecordingNotifier(IHubContext<DispatchHub> hub) : IDispatchNotifier
{
    private readonly SignalRDispatchNotifier _inner = new(hub, NullLogger<SignalRDispatchNotifier>.Instance);
    public readonly ConcurrentQueue<string> Events = new();

    public Task JobCreatedAsync(JobDto job, CancellationToken ct = default) { Events.Enqueue("created"); return _inner.JobCreatedAsync(job, ct); }
    public Task JobStatusChangedAsync(JobStatusChangedEvent e, CancellationToken ct = default) { Events.Enqueue($"status:{e.Status}"); return _inner.JobStatusChangedAsync(e, ct); }
    public Task JobProgressAsync(JobProgressEvent e, CancellationToken ct = default) { Events.Enqueue("progress"); return _inner.JobProgressAsync(e, ct); }
    public Task DriverUpdatedAsync(DriverDto driver, CancellationToken ct = default) { Events.Enqueue($"driver:{driver.Status}"); return _inner.DriverUpdatedAsync(driver, ct); }
}

/// <summary>Boots the real app against a throwaway SQLite file. Simulation is off unless requested.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly bool _simulation;
    public ApiFactory() : this(false) { }
    protected ApiFactory(bool simulation) => _simulation = simulation;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dispatch-test-{Guid.NewGuid():N}.db");

    public RecordingNotifier Notifier => Services.GetRequiredService<RecordingNotifier>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Dispatch", $"Data Source={_dbPath}");
        builder.UseSetting("Simulation:Enabled", _simulation.ToString());
        builder.UseSetting("Simulation:AutoAssign", _simulation.ToString());
        builder.UseSetting("Simulation:TickInterval", "00:00:00.050");
        builder.UseSetting("Simulation:TimeScale", "1000");
        builder.UseSetting("Simulation:PickupDwell", "00:00:00");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDispatchNotifier>();
            services.AddSingleton<RecordingNotifier>();
            services.AddSingleton<IDispatchNotifier>(sp => sp.GetRequiredService<RecordingNotifier>());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _dbPath + "-journal" })
            try { File.Delete(f); } catch { /* best effort */ }
    }

    // ---- helpers ----
    public static object NewJobBody(string customer = "Acme") => new
    {
        customerName = customer,
        pickup = new { lat = 40.7128, lng = -74.0060 },
        dropoff = new { lat = 40.7580, lng = -73.9855 },
    };

    public async Task<JobDto> CreateJobAsync(HttpClient client, string customer = "Acme")
    {
        var res = await client.PostAsJsonAsync("/api/jobs", NewJobBody(customer));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JobDto>(Json))!;
    }

    public async Task<List<DriverDto>> GetDriversAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<DriverDto>>("/api/drivers", Json))!;
}
