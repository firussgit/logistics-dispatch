using LogisticsDispatch.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LogisticsDispatch.Api.Controllers;

public record DriverAccountDto(Guid UserId, Guid DriverId, string Email, string DisplayName, DateTimeOffset CreatedAt);

/// <summary>Account management. Dispatchers only: driver accounts are never self-service.</summary>
[ApiController]
[Authorize(Roles = Roles.Dispatcher)]
[Route("api/admin/drivers")]
public class AdminController(
    IUserRepository users,
    IDriverRepository drivers,
    IUnitOfWork uow,
    IPasswordHasher<User> hasher,
    IDispatchNotifier notifier,
    TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<DriverAccountDto>> List(CancellationToken ct) =>
        (await users.ListByRoleAsync(UserRole.Driver, ct))
            .Where(u => u.DriverId.HasValue)
            .Select(u => new DriverAccountDto(u.Id, u.DriverId!.Value, u.Email, u.DisplayName, u.CreatedAt));

    [HttpPost]
    public async Task<ActionResult<DriverAccountDto>> Create(CreateDriverAccountRequest req, CancellationToken ct)
    {
        var email = Core.Entities.User.NormalizeEmail(req.Email);
        if (await users.GetByEmailAsync(email, ct) is not null)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Email already registered", detail: "An account with that email already exists.");

        var name = req.DisplayName.Trim();
        // starts in the middle of the service area; the first position update moves it
        var driver = new Driver { Name = name, IsAutomated = false, CurrentLocation = new Location(40.7128, -74.006) };
        var user = new User { Email = email, DisplayName = name, Role = UserRole.Driver, DriverId = driver.Id, CreatedAt = time.GetUtcNow() };
        user.PasswordHash = hasher.HashPassword(user, req.Password);

        drivers.Add(driver);
        users.Add(user);
        await uow.SaveChangesAsync(ct);

        await notifier.DriverUpdatedAsync(DriverDto.From(driver), ct);
        return Created($"api/admin/drivers/{user.Id}", new DriverAccountDto(user.Id, driver.Id, user.Email, user.DisplayName, user.CreatedAt));
    }
}
