using System.Security.Claims;
using LogisticsDispatch.Core.Entities;
using LogisticsDispatch.Core.Security;

namespace LogisticsDispatch.Core.Tests;

public class SecurityTests
{
    private static readonly Guid CustomerA = Guid.NewGuid();
    private static readonly Guid CustomerB = Guid.NewGuid();
    private static readonly Guid DriverA = Guid.NewGuid();
    private static readonly Guid DriverB = Guid.NewGuid();

    private static ClaimsPrincipal User(string role, Guid? userId = null, Guid? driverId = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()), new(ClaimTypes.Role, role), new(ClaimTypes.Name, "Someone") };
        if (driverId is { } d) claims.Add(new Claim(AppClaims.DriverId, d.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void Dispatchers_can_view_and_operate_everything()
    {
        var dispatcher = User(Roles.Dispatcher);
        Assert.True(dispatcher.CanView(CustomerA, DriverA));
        Assert.True(dispatcher.CanView(null, null));
        Assert.True(dispatcher.CanOperate(DriverA));
        Assert.True(dispatcher.CanOperate(null));
    }

    [Fact]
    public void Customers_can_view_only_jobs_they_own_and_never_operate_them()
    {
        var customer = User(Roles.Customer, CustomerA);
        Assert.True(customer.CanView(CustomerA, DriverA));
        Assert.False(customer.CanView(CustomerB, DriverA));
        Assert.False(customer.CanView(null, DriverA));   // staff-booked job with no owner
        Assert.False(customer.CanOperate(DriverA));
    }

    [Fact]
    public void Drivers_can_view_and_operate_only_jobs_assigned_to_them()
    {
        var driver = User(Roles.Driver, driverId: DriverA);
        Assert.True(driver.CanView(CustomerA, DriverA));
        Assert.True(driver.CanOperate(DriverA));
        Assert.False(driver.CanView(CustomerA, DriverB));
        Assert.False(driver.CanView(CustomerA, null));
        Assert.False(driver.CanOperate(DriverB));
        Assert.False(driver.CanOperate(null));
    }

    [Fact]
    public void A_driver_account_with_no_driver_claim_has_no_access_even_to_unassigned_jobs()
    {
        var broken = User(Roles.Driver);
        Assert.False(broken.CanView(CustomerA, null));
        Assert.False(broken.CanOperate(null));
    }

    [Fact]
    public void A_role_is_not_an_identity_a_customer_claiming_a_driver_id_gains_nothing()
    {
        var sneaky = User(Roles.Customer, CustomerB, driverId: DriverA);
        Assert.False(sneaky.CanOperate(DriverA));
        Assert.False(sneaky.CanView(CustomerA, DriverA));
    }

    [Fact]
    public void Claim_accessors_tolerate_missing_or_garbage_values()
    {
        var empty = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.Null(empty.UserId());
        Assert.Null(empty.DriverId());
        Assert.Equal("", empty.DisplayName());

        var garbage = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "not-a-guid"), new Claim(AppClaims.DriverId, "nope")], "test"));
        Assert.Null(garbage.UserId());
        Assert.Null(garbage.DriverId());
    }

    [Fact]
    public void Emails_are_normalised_for_uniqueness() =>
        Assert.Equal("pat@example.test", Entities.User.NormalizeEmail("  Pat@Example.TEST "));
}
