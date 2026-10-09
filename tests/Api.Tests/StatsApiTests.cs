using System.Net.Http.Json;
using LogisticsDispatch.Core.Models;

namespace LogisticsDispatch.Api.Tests;

public class StatsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.DispatcherClient();

    [Fact]
    public async Task Rush_hour_creates_the_requested_orders_and_stats_count_them()
    {
        var before = (await _client.GetFromJsonAsync<StatsDto>("/api/stats?hours=1", ApiFactory.Json))!;
        var res = await _client.PostAsync("/api/stats/rush?count=7", null);
        res.EnsureSuccessStatusCode();
        var after = (await _client.GetFromJsonAsync<StatsDto>("/api/stats?hours=1", ApiFactory.Json))!;

        Assert.Equal(before.Orders + 7, after.Orders);
        Assert.Single(after.PerHour);
    }

    [Fact]
    public async Task Rush_count_and_window_are_clamped()
    {
        (await _client.PostAsync("/api/stats/rush?count=100000", null)).EnsureSuccessStatusCode();
        var s = (await _client.GetFromJsonAsync<StatsDto>("/api/stats?hours=999999", ApiFactory.Json))!;
        Assert.Equal(168, s.WindowHours);
        Assert.True(s.Orders >= 1 && s.Orders <= 5000);
    }
}
