using System.Net;
using System.Net.Http.Json;
using LogisticsDispatch.Core.Entities;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Models;
using LogisticsDispatch.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LogisticsDispatch.Api.Tests;

public class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.test";

    private static Task<HttpResponseMessage> Register(HttpClient c, string email, string password = "Abcdef12", string name = "Pat Example", object? extra = null) =>
        c.PostAsJsonAsync("/api/auth/register", extra ?? new { email, displayName = name, password });

    [Fact]
    public async Task Registering_creates_a_customer_and_signs_them_in()
    {
        var client = factory.CreateClient();
        var email = NewEmail();

        var res = await Register(client, email);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var me = await res.Content.ReadFromJsonAsync<MeDto>(ApiFactory.Json);
        Assert.Equal(UserRole.Customer, me!.Role);
        Assert.Equal(email, me.Email);
        Assert.Null(me.Driver);

        var whoAmI = await client.GetFromJsonAsync<MeDto>("/api/me", ApiFactory.Json); // the cookie from register is enough
        Assert.Equal(me.Id, whoAmI!.Id);
    }

    [Fact]
    public async Task Nobody_can_self_register_as_a_dispatcher_or_driver()
    {
        var client = factory.CreateClient();
        var email = NewEmail();
        var res = await Register(client, email, extra: new { email, displayName = "Sneaky", password = "Abcdef12", role = "Dispatcher", driverId = Guid.NewGuid() });

        var me = await res.Content.ReadFromJsonAsync<MeDto>(ApiFactory.Json);
        Assert.Equal(UserRole.Customer, me!.Role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/drivers")).StatusCode);
    }

    [Fact]
    public async Task Email_is_case_insensitive_and_unique()
    {
        var email = NewEmail();
        Assert.Equal(HttpStatusCode.OK, (await Register(factory.CreateClient(), email)).StatusCode);

        var again = await Register(factory.CreateClient(), email.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Theory]
    [InlineData("short1")]          // too short
    [InlineData("nodigitshere")]    // no digit
    [InlineData("12345678")]        // no letter
    public async Task Weak_passwords_are_rejected(string password) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Register(factory.CreateClient(), NewEmail(), password)).StatusCode);

    [Theory]
    [InlineData("not-an-email", "Pat")]
    [InlineData("ok@example.test", "")]
    [InlineData("ok@example.test", "   ")]
    public async Task Invalid_email_or_name_is_rejected(string email, string name) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Register(factory.CreateClient(), email, name: name)).StatusCode);

    [Fact]
    public async Task Login_works_and_the_wrong_password_and_unknown_email_look_identical()
    {
        var client = factory.CreateClient();
        var ok = await client.PostAsJsonAsync("/api/auth/login", new { email = DbSeeder.DemoDispatcherEmail.ToUpperInvariant(), password = ApiFactory.DemoPassword });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(UserRole.Dispatcher, (await ok.Content.ReadFromJsonAsync<MeDto>(ApiFactory.Json))!.Role);

        var wrongPassword = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = DbSeeder.DemoDispatcherEmail, password = "nope-nope-1" });
        var unknownEmail = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "ghost@example.test", password = "nope-nope-1" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        var a = await wrongPassword.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var b = await unknownEmail.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(a.GetProperty("detail").GetString(), b.GetProperty("detail").GetString()); // no hint which part was wrong
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
        Assert.DoesNotContain("Set-Cookie", wrongPassword.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var client = factory.DispatcherClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/jobs")).StatusCode);
    }

    [Fact]
    public async Task The_auth_cookie_is_httponly_and_samesite()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email = DbSeeder.DemoCustomerEmail, password = ApiFactory.DemoPassword });
        var cookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("dispatch.auth=")).ToLowerInvariant();

        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.DoesNotContain("expires=", cookie); // session cookie: gone when the browser closes
    }

    [Fact]
    public async Task Driver_accounts_carry_their_driver_in_me()
    {
        var me = await factory.DriverClient().GetFromJsonAsync<MeDto>("/api/me", ApiFactory.Json);
        Assert.Equal(UserRole.Driver, me!.Role);
        Assert.NotNull(me.Driver);
        Assert.False(me.Driver!.IsAutomated);
    }

    [Fact]
    public async Task Passwords_are_stored_hashed_never_as_typed()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
        var user = db.Users.Single(u => u.Email == DbSeeder.DemoDispatcherEmail);

        Assert.DoesNotContain(ApiFactory.DemoPassword, user.PasswordHash);
        Assert.True(user.PasswordHash.Length > 40);
    }

    [Theory]
    [InlineData("/api/me")]
    [InlineData("/api/jobs")]
    [InlineData("/api/drivers")]
    [InlineData("/api/offers")]
    [InlineData("/api/status")]
    [InlineData("/api/routing")]
    public async Task Everything_but_login_pages_and_tracking_needs_a_session(string url) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(url)).StatusCode);

    [Fact]
    public async Task Static_pages_stay_public_so_the_login_page_can_load()
    {
        var client = factory.CreateClient();
        foreach (var page in new[] { "/login.html", "/styles.css", "/auth.js" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(page)).StatusCode);
    }

    [Fact]
    public async Task Demo_endpoint_lists_the_seeded_accounts_when_demo_users_are_on()
    {
        var json = await factory.CreateClient().GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/demo");
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, json.GetProperty("accounts").GetArrayLength());
    }
}

/// <summary>Brute-forcing passwords: after a handful of attempts a client gets 429 instead of more guesses.</summary>
public class LoginRateLimitTests : IClassFixture<LoginRateLimitTests.StrictFactory>
{
    public class StrictFactory : ApiFactory
    {
        protected override IEnumerable<(string, string)> ExtraSettings => [("Auth:AttemptsPerMinute", "4")];
    }

    private readonly StrictFactory _factory;
    public LoginRateLimitTests(StrictFactory factory) => _factory = factory;

    [Fact]
    public async Task Too_many_login_attempts_are_throttled()
    {
        var client = _factory.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++)
            codes.Add((await client.PostAsJsonAsync("/api/auth/login", new { email = "victim@example.test", password = $"guess-{i}-abc" })).StatusCode);

        Assert.Equal(4, codes.Count(c => c == HttpStatusCode.Unauthorized)); // the allowed attempts were real (and failed)
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

}

/// <summary>Production-style config: no demo accounts exist and nothing advertises them.</summary>
public class NoDemoUsersTests : IClassFixture<NoDemoUsersTests.ProductionLikeFactory>
{
    public class ProductionLikeFactory : ApiFactory
    {
        protected override IEnumerable<(string, string)> ExtraSettings => [("Seed:DemoUsers", "false")];
    }

    private readonly ProductionLikeFactory _factory;
    public NoDemoUsersTests(ProductionLikeFactory factory) => _factory = factory;

    [Fact]
    public async Task No_demo_accounts_are_seeded_and_the_demo_endpoint_says_so()
    {
        var client = _factory.CreateClient();

        var demo = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/demo");
        Assert.False(demo.GetProperty("enabled").GetBoolean());
        Assert.False(demo.TryGetProperty("password", out _));

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = DbSeeder.DemoDispatcherEmail, password = ApiFactory.DemoPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }
}
