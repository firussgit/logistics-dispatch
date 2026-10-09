using System.Security.Claims;

namespace LogisticsDispatch.Core.Security;

public static class Roles
{
    public const string Customer = nameof(UserRole.Customer);
    public const string Dispatcher = nameof(UserRole.Dispatcher);
    public const string Driver = nameof(UserRole.Driver);
}

public static class AppClaims
{
    /// <summary>The driver a Driver-role user operates.</summary>
    public const string DriverId = "driver_id";
}

public static class ClaimsPrincipalExtensions
{
    public static Guid? UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    public static Guid? DriverId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(AppClaims.DriverId)?.Value, out var id) ? id : null;

    public static string DisplayName(this ClaimsPrincipal user) => user.FindFirst(ClaimTypes.Name)?.Value ?? "";

    public static bool IsDispatcher(this ClaimsPrincipal user) => user.IsInRole(Roles.Dispatcher);

    /// <summary>Dispatchers see everything; customers their own orders; drivers the jobs assigned to them.</summary>
    public static bool CanView(this ClaimsPrincipal user, Guid? jobCustomerId, Guid? jobDriverId) =>
        user.IsDispatcher()
        || (user.IsInRole(Roles.Customer) && jobCustomerId is { } c && user.UserId() == c)
        || (user.IsInRole(Roles.Driver) && jobDriverId is { } d && user.DriverId() == d);

    /// <summary>Who may move a job through its lifecycle (start/complete): dispatchers and the assigned driver.</summary>
    public static bool CanOperate(this ClaimsPrincipal user, Guid? jobDriverId) =>
        user.IsDispatcher() || (user.IsInRole(Roles.Driver) && jobDriverId is { } d && user.DriverId() == d);
}
