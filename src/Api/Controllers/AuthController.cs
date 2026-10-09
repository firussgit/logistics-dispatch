using System.Security.Claims;
using LogisticsDispatch.Api.Contracts;
using LogisticsDispatch.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace LogisticsDispatch.Api.Controllers;

[ApiController]
[Route("api")]
public class AuthController(
    IUserRepository users,
    IDriverRepository drivers,
    IUnitOfWork uow,
    IPasswordHasher<User> hasher,
    TimeProvider time,
    IOptions<SeedOptions> seed) : ControllerBase
{
    // Verified against when the email is unknown, so "no such user" and "wrong password" take the same time.
    private static readonly User Decoy = new();
    private static readonly string DecoyHash = new PasswordHasher<User>().HashPassword(Decoy, Guid.NewGuid().ToString("N"));

    /// <summary>Customers sign themselves up. Dispatcher and driver accounts are never self-service.</summary>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("auth/register")]
    public async Task<ActionResult<MeDto>> Register(RegisterRequest req, CancellationToken ct)
    {
        var email = Core.Entities.User.NormalizeEmail(req.Email);
        if (await users.GetByEmailAsync(email, ct) is not null)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Email already registered", detail: "An account with that email already exists. Try signing in.");

        var user = new User { Email = email, DisplayName = req.DisplayName.Trim(), Role = UserRole.Customer, CreatedAt = time.GetUtcNow() };
        user.PasswordHash = hasher.HashPassword(user, req.Password);
        users.Add(user);
        await uow.SaveChangesAsync(ct);

        await SignInAsync(user);
        return await ToMeAsync(user, ct);
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("auth/login")]
    public async Task<ActionResult<MeDto>> Login(LoginRequest req, CancellationToken ct)
    {
        var user = await users.GetByEmailAsync(Core.Entities.User.NormalizeEmail(req.Email), ct);
        var result = hasher.VerifyHashedPassword(user ?? Decoy, user?.PasswordHash ?? DecoyHash, req.Password);
        if (user is null || result == PasswordVerificationResult.Failed)
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed", detail: "Invalid email or password.");

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            await uow.SaveChangesAsync(ct);
        }

        await SignInAsync(user);
        return await ToMeAsync(user, ct);
    }

    [Authorize]
    [HttpPost("auth/logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<MeDto>> Me(CancellationToken ct)
    {
        var user = User.UserId() is { } id ? await users.GetAsync(id, ct) : null;
        if (user is null)
        {
            // account deleted since the cookie was issued
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Unauthorized();
        }
        return await ToMeAsync(user, ct);
    }

    /// <summary>Lets the login page offer one-click demo sign-ins. Only answers when demo accounts are seeded (development).</summary>
    [AllowAnonymous]
    [HttpGet("auth/demo")]
    public object Demo() => !seed.Value.DemoUsers
        ? new { enabled = false }
        : new
        {
            enabled = true,
            password = seed.Value.DemoPassword,
            accounts = new[]
            {
                new { role = "Dispatcher", email = DbSeeder.DemoDispatcherEmail },
                new { role = "Driver", email = DbSeeder.DemoDriverEmail },
                new { role = "Customer", email = DbSeeder.DemoCustomerEmail },
            }
        };

    private async Task SignInAsync(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString()),
        };
        if (user.DriverId is { } driverId) claims.Add(new Claim(AppClaims.DriverId, driverId.ToString()));

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false });
    }

    private async Task<MeDto> ToMeAsync(User user, CancellationToken ct)
    {
        DriverDto? driver = null;
        if (user.DriverId is { } id && await drivers.GetAsync(id, ct) is { } d) driver = DriverDto.From(d);
        return new MeDto(user.Id, user.Email, user.DisplayName, user.Role, driver);
    }
}
