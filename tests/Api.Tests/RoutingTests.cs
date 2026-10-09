using System.Net;
using System.Net.Http.Json;
using LogisticsDispatch.Core.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using LogisticsDispatch.Core.Entities;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Models;
using LogisticsDispatch.Core.Routing;
using LogisticsDispatch.Core.Services;
using LogisticsDispatch.Infrastructure.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LogisticsDispatch.Api.Tests;

public class OsrmRouteProviderTests
{
    private const string OkResponse = """
        {"code":"Ok","routes":[{"distance":1234.5,"duration":200,
          "geometry":{"type":"LineString","coordinates":[[-74.006,40.7128],[-74.005,40.7140],[-74.002,40.7150]]}}],"waypoints":[]}
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public Uri? LastUri;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            throw new HttpRequestException("connection refused");
        }
    }

    private static (OsrmRouteProvider Provider, RoutingHealth Health) Create(HttpMessageHandler handler)
    {
        var health = new RoutingHealth(TimeProvider.System);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://osrm.test/") };
        var opts = Options.Create(new RoutingOptions { FailureCooldownSeconds = 60 });
        return (new OsrmRouteProvider(http, opts, health, NullLogger<OsrmRouteProvider>.Instance), health);
    }

    private static readonly Location A = new(40.7128, -74.006);
    private static readonly Location B = new(40.715, -74.002);

    [Fact]
    public async Task Parses_geojson_into_lat_lng_points_and_uses_osrm_distance()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OkResponse) });
        var (provider, health) = Create(handler);

        var route = await provider.GetRouteAsync(A, B);

        Assert.False(route.IsStraightLine);
        Assert.Equal(3, route.Points.Count);
        Assert.Equal(new Location(40.7140, -74.005), route.Points[1]); // GeoJSON is [lng, lat]
        Assert.Equal(1234.5, route.DistanceMeters);
        Assert.Equal("up", health.State);
        // request is lng,lat;lng,lat with full geojson geometry
        Assert.Contains("route/v1/driving/-74.006,40.7128;-74.002,40.715", handler.LastUri!.ToString());
        Assert.Contains("geometries=geojson", handler.LastUri.ToString());
        Assert.Contains("overview=full", handler.LastUri.ToString());
    }

    [Fact]
    public async Task Falls_back_to_a_straight_line_when_the_server_is_unreachable_and_stops_retrying_during_cooldown()
    {
        var handler = new FailingHandler();
        var (provider, health) = Create(handler);

        var first = await provider.GetRouteAsync(A, B);
        var second = await provider.GetRouteAsync(A, B);

        Assert.True(first.IsStraightLine);
        Assert.True(second.IsStraightLine);
        Assert.Equal(1, handler.Calls); // the second call skipped the engine entirely
        Assert.Equal("down", health.State);
        Assert.True(health.InCooldown);
    }

    [Theory]
    [InlineData("""{"code":"NoRoute","message":"Impossible route between points"}""")]
    [InlineData("""{"code":"Ok","routes":[]}""")]
    public async Task No_route_found_falls_back_to_a_straight_line_without_marking_the_server_down(string body)
    {
        var (provider, health) = Create(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));

        var route = await provider.GetRouteAsync(A, B);

        Assert.True(route.IsStraightLine);
        Assert.Equal("up", health.State);
        Assert.False(health.InCooldown);
    }

    [Fact]
    public async Task Server_error_status_falls_back_to_a_straight_line()
    {
        var (provider, _) = Create(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") }));
        Assert.True((await provider.GetRouteAsync(A, B)).IsStraightLine);
    }

    [Fact]
    public void Very_long_geometries_are_decimated_but_keep_both_endpoints()
    {
        var coords = string.Join(",", Enumerable.Range(0, 3000).Select(i => FormattableString.Invariant($"[{-74 + i * 0.0001},{40 + i * 0.0001}]")));
        var json = "{\"code\":\"Ok\",\"routes\":[{\"distance\":5000,\"geometry\":{\"coordinates\":[" + coords + "]}}]}";

        var route = OsrmRouteProvider.ParseResponse(json)!;

        Assert.Equal(800, route.Points.Count);
        Assert.Equal(new Location(40, -74), route.Points[0]);
        Assert.Equal(new Location(40 + 2999 * 0.0001, -74 + 2999 * 0.0001).Lat, route.Points[^1].Lat, 6);
    }
}

/// <summary>Proves the worker drives along the road path, not the straight line, using a provider that always returns an "L".</summary>
public class RouteFollowingTests : IClassFixture<RouteFollowingTests.BentRouteFactory>
{
    /// <summary>pickup → corner (same lat as pickup, lng of dropoff) → dropoff, so the road is clearly longer than the crow-flies line.</summary>
    public sealed class BentRouteProvider : IRouteProvider
    {
        public Task<Route> GetRouteAsync(Location from, Location to, CancellationToken ct = default)
        {
            var corner = new Location(from.Lat, to.Lng);
            IReadOnlyList<Location> pts = [from, corner, to];
            return Task.FromResult(new Route(pts, RoutePath.Length(pts)));
        }
    }

    public class BentRouteFactory : ApiFactory
    {
        public BentRouteFactory() : base(simulation: true) { }

        // 75 m per 50 ms tick: slow enough that polling reliably sees the driver on each leg of the L.
        protected override IEnumerable<(string, string)> ExtraSettings => [("Simulation:TimeScale", "100")];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IRouteProvider, BentRouteProvider>()));
        }
    }

    private readonly BentRouteFactory _factory;
    private readonly HttpClient _client;

    public RouteFollowingTests(BentRouteFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Trip_route_is_exposed_over_the_api_and_the_driver_follows_it_to_the_end()
    {
        var created = await _factory.CreateJobAsync(_client, "Road trip");
        var straightMeters = GeoMath.DistanceMeters(created.Pickup, created.Dropoff);

        // 1) the route appears (a few ticks after creation) as three points: the L
        RouteDto? route = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            route = await _client.GetFromJsonAsync<RouteDto>($"/api/jobs/{created.Id}/route", ApiFactory.Json);
            if (route!.Trip is not null) break;
            await Task.Delay(50);
        }
        Assert.NotNull(route!.Trip);
        Assert.Equal(3, route.Trip!.Count);
        Assert.Equal(created.Pickup.Lat, route.Trip[1][0], 6); // the corner keeps the pickup's latitude

        // 2) while in transit the driver is always ON the L: never on the diagonal across it
        var seenOnFirstLeg = false;
        var seenOnSecondLeg = false;
        JobDto? job = null;
        while (DateTime.UtcNow < deadline)
        {
            job = await _client.GetFromJsonAsync<JobDto>($"/api/jobs/{created.Id}", ApiFactory.Json);
            if (job!.Status == JobStatus.InTransit)
            {
                var p = job.CurrentLocation;
                var onFirstLeg = Math.Abs(p.Lat - created.Pickup.Lat) < 1e-5;       // along the pickup's latitude
                var onSecondLeg = Math.Abs(p.Lng - created.Dropoff.Lng) < 1e-5;     // along the dropoff's longitude
                Assert.True(onFirstLeg || onSecondLeg, $"driver left the road at {p}");
                seenOnFirstLeg |= onFirstLeg && !onSecondLeg;
                seenOnSecondLeg |= onSecondLeg && !onFirstLeg;
            }
            if (job.Status == JobStatus.Completed) break;
            await Task.Delay(30);
        }

        Assert.Equal(JobStatus.Completed, job!.Status);
        Assert.True(seenOnFirstLeg && seenOnSecondLeg, "expected to observe the driver on both legs of the L");
        Assert.True(RoutePath.Length(route.Trip.Select(p => new Location(p[0], p[1])).ToList()) > straightMeters * 1.05,
            "the road route should be longer than the crow-flies distance");
        Assert.Contains("route:Trip", _factory.Notifier.Events);
    }

    [Fact]
    public async Task Assigned_driver_gets_an_approach_route_and_arrives_before_the_trip_starts()
    {
        var created = await _factory.CreateJobAsync(_client, "Approach");

        RouteDto? route = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            route = await _client.GetFromJsonAsync<RouteDto>($"/api/jobs/{created.Id}/route", ApiFactory.Json);
            var job = await _client.GetFromJsonAsync<JobDto>($"/api/jobs/{created.Id}", ApiFactory.Json);
            if (route!.Approach is not null || job!.Status >= JobStatus.InTransit) break;
            await Task.Delay(20);
        }

        Assert.NotNull(route!.Approach);
        Assert.Equal(3, route.Approach!.Count);
        Assert.Contains("route:Approach", _factory.Notifier.Events);
    }

    [Fact]
    public async Task Route_endpoint_returns_404_for_unknown_jobs()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}/route")).StatusCode);
    }
}

public class RoutingStatusTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Status_reports_straight_line_mode_when_no_engine_is_configured()
    {
        var json = await factory.CreateClient().GetFromJsonAsync<System.Text.Json.JsonElement>("/api/routing");
        Assert.Equal("StraightLine", json.GetProperty("provider").GetString());
        Assert.Equal("straight-line", json.GetProperty("mode").GetString());
    }
}
