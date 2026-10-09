namespace LogisticsDispatch.Core.Entities;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Lower-cased and trimmed; unique.</summary>
    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }

    /// <summary>For driver accounts: the driver this user operates.</summary>
    public Guid? DriverId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
