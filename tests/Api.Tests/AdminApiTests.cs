using System.Net;
using System.Net.Http.Json;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Models;

namespace LogisticsDispatch.Api.Tests;

public class AdminApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static object Body(string email, string password = "Secret123") => new { displayName = "  New Driver ", email, password };

    [Fact]
    public async Task Dispatcher_creates_a_driver_who_can_sign_in_and_appears_in_the_fleet()
    {
        var dispatcher = factory.DispatcherClient();
        var email = $"new-{Guid.NewGuid():N}@example.test";

        var res = await dispatcher.PostAsJsonAsync("/api/admin/drivers", Body(email.ToUpperInvariant()));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var driverClient = factory.LoginClient(email, "Secret123");
        var me = (await driverClient.GetFromJsonAsync<MeDto>("/api/me", ApiFactory.Json))!;
        Assert.Equal("New Driver", me.DisplayName);
        Assert.NotNull(me.Driver);
        Assert.False(me.Driver!.IsAutomated);
        Assert.Equal(DriverStatus.Idle, me.Driver.Status);

        var fleet = await factory.GetDriversAsync(dispatcher);
        Assert.Contains(fleet, d => d.Id == me.Driver.Id);

        var accounts = await dispatcher.GetFromJsonAsync<List<System.Text.Json.JsonElement>>("/api/admin/drivers");
        Assert.Contains(accounts!, a => a.GetProperty("email").GetString() == email);
    }

    [Fact]
    public async Task Duplicate_email_and_weak_password_are_rejected()
    {
        var dispatcher = factory.DispatcherClient();
        var email = $"dup-{Guid.NewGuid():N}@example.test";
        (await dispatcher.PostAsJsonAsync("/api/admin/drivers", Body(email))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await dispatcher.PostAsJsonAsync("/api/admin/drivers", Body(email))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await dispatcher.PostAsJsonAsync("/api/admin/drivers", Body($"x-{Guid.NewGuid():N}@example.test", "short"))).StatusCode);
    }

    [Fact]
    public async Task Only_dispatchers_may_manage_driver_accounts()
    {
        var email = $"nope-{Guid.NewGuid():N}@example.test";
        foreach (var client in new[] { factory.DriverClient(), factory.DemoCustomerClient() })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/drivers", Body(email))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/drivers")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/admin/drivers")).StatusCode);
    }
}
