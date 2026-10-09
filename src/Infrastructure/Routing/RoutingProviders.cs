using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogisticsDispatch.Infrastructure.Routing;

public class RoutingOptions
{
    public const string Section = "Routing";

    /// <summary>"Osrm" (real street routes from an OSRM server) or "StraightLine" (no routing engine).</summary>
    public string Provider { get; set; } = "Osrm";

    /// <summary>Base URL of the OSRM server (see /routing for a docker-compose setup).</summary>
    public string BaseUrl { get; set; } = "http://localhost:5000";

    public int TimeoutSeconds { get; set; } = 3;

    /// <summary>After a connection failure, skip OSRM for this long and use straight lines instead of paying the timeout per job.</summary>
    public int FailureCooldownSeconds { get; set; } = 20;
}

/// <summary>Shared (singleton) record of whether the routing engine is answering; surfaced at <c>/api/routing</c>.</summary>
public class RoutingHealth(TimeProvider time)
{
    private long _unavailableUntilTicks;
    private volatile int _state; // 0 unknown, 1 up, 2 down

    public bool InCooldown => time.GetUtcNow().UtcTicks < Interlocked.Read(ref _unavailableUntilTicks);
    public string State => _state switch { 1 => "up", 2 => "down", _ => "unknown" };

    public void ReportSuccess() => _state = 1;

    public void ReportFailure(TimeSpan cooldown)
    {
        _state = 2;
        Interlocked.Exchange(ref _unavailableUntilTicks, (time.GetUtcNow() + cooldown).UtcTicks);
    }
}

/// <summary>No routing engine: every path is A→B. Keeps the whole app working offline and in tests.</summary>
public class StraightLineRouteProvider : IRouteProvider
{
    public Task<Route> GetRouteAsync(Location from, Location to, CancellationToken ct = default) =>
        Task.FromResult(Route.StraightLine(from, to));
}

/// <summary>Street routes from an OSRM server (<c>/route/v1/driving</c>, GeoJSON geometry). Falls back to a straight line on any failure.</summary>
public class OsrmRouteProvider(HttpClient http, IOptions<RoutingOptions> options, RoutingHealth health, ILogger<OsrmRouteProvider> logger) : IRouteProvider
{
    private const int MaxPoints = 800;

    public async Task<Route> GetRouteAsync(Location from, Location to, CancellationToken ct = default)
    {
        if (health.InCooldown) return Route.StraightLine(from, to);

        var url = FormattableString.Invariant(
            $"route/v1/driving/{from.Lng:0.######},{from.Lat:0.######};{to.Lng:0.######},{to.Lat:0.######}?overview=full&geometries=geojson");
        try
        {
            using var response = await http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            health.ReportSuccess(); // the server answered, even if it found no route

            var route = response.IsSuccessStatusCode ? ParseResponse(body) : null;
            if (route is null)
            {
                logger.LogWarning("OSRM returned no usable route ({Status}) for {From} → {To}; using a straight line.", (int)response.StatusCode, from, to);
                return Route.StraightLine(from, to);
            }
            return route;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            var cooldown = TimeSpan.FromSeconds(options.Value.FailureCooldownSeconds);
            health.ReportFailure(cooldown);
            logger.LogWarning("OSRM unreachable at {BaseAddress} ({Reason}); using straight lines for {Cooldown}s. Start it with routing/setup.ps1.",
                http.BaseAddress, ex.Message, cooldown.TotalSeconds);
            return Route.StraightLine(from, to);
        }
    }

    /// <summary>Parses an OSRM route response; null if it holds no route.</summary>
    public static Route? ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.GetString() != "Ok") return null;
        if (!root.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0) return null;

        var best = routes[0];
        var coords = best.GetProperty("geometry").GetProperty("coordinates");
        var points = new List<Location>(coords.GetArrayLength());
        foreach (var c in coords.EnumerateArray())
            points.Add(new Location(c[1].GetDouble(), c[0].GetDouble())); // GeoJSON is [lng, lat]
        if (points.Count < 2) return null;

        if (points.Count > MaxPoints)
        {
            var step = (double)(points.Count - 1) / (MaxPoints - 1);
            points = Enumerable.Range(0, MaxPoints).Select(i => points[(int)Math.Round(i * step)]).ToList();
        }
        var distance = best.TryGetProperty("distance", out var d) ? d.GetDouble() : RoutePath.Length(points);
        return new Route(points, distance);
    }
}
