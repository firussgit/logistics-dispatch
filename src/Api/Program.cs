using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LogisticsDispatch.Api.Middleware;
using LogisticsDispatch.Infrastructure;
using LogisticsDispatch.Infrastructure.Data;
using LogisticsDispatch.Infrastructure.Realtime;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration);

// ---- authentication: a cookie session (also carries SignalR/WebSocket connections) ----
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "dispatch.auth";
        o.Cookie.HttpOnly = true;                              // not readable from JavaScript
        o.Cookie.SameSite = SameSiteMode.Lax;                  // not sent on cross-site POSTs (CSRF defence for this JSON API)
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // Always in production behind HTTPS
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        // an API, not a website: answer 401/403 instead of redirecting to a login page
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });

// Secure by default: every endpoint (controllers and hubs) needs a signed-in user unless it opts out with [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Slow down password guessing: per-client-address fixed window on login/register.
var authPermits = builder.Configuration.GetValue("Auth:AttemptsPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPermits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var db = sp.GetRequiredService<DispatchDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(db, sp.GetRequiredService<IPasswordHasher<LogisticsDispatch.Core.Entities.User>>(),
        sp.GetRequiredService<IOptions<SeedOptions>>().Value);
}

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();   // the HTML pages are public; all data behind them is not

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapHub<DispatchHub>("/hubs/dispatch");                  // needs sign-in (fallback policy + [Authorize])
app.MapHub<TrackingHub>("/hubs/track").AllowAnonymous();    // tracking links: no account, one delivery per token

app.Run();

public partial class Program;
