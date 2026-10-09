using System.ComponentModel.DataAnnotations;
using LogisticsDispatch.Core.Entities;

namespace LogisticsDispatch.Api.Contracts;

public class LocationInput
{
    [Range(-90, 90)] public double Lat { get; set; }
    [Range(-180, 180)] public double Lng { get; set; }

    public Location ToLocation() => new(Lat, Lng);
}

public class CreateJobRequest : IValidatableObject
{
    /// <summary>Required when a dispatcher books on someone's behalf; ignored for customers (their own name is used).</summary>
    [StringLength(100)]
    public string? CustomerName { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    [Required] public LocationInput? Pickup { get; set; }
    [Required] public LocationInput? Dropoff { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Pickup is not null && Dropoff is not null && Pickup.Lat == Dropoff.Lat && Pickup.Lng == Dropoff.Lng)
            yield return new ValidationResult("Pickup and dropoff must differ.", [nameof(Dropoff)]);
    }
}

public class AssignJobRequest
{
    [Required] public Guid? DriverId { get; set; }
}

public class LoginRequest
{
    [Required, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(200)] public string Password { get; set; } = "";
}

/// <summary>Dispatcher creates an account for a human driver; the driver profile is created with it.</summary>
public class CreateDriverAccountRequest : IValidatableObject
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(60, MinimumLength = 1)] public string DisplayName { get; set; } = "";
    [Required, StringLength(200)] public string Password { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        new RegisterRequest { Email = Email, DisplayName = DisplayName, Password = Password }.Validate(validationContext);
}

public class RegisterRequest : IValidatableObject
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(60, MinimumLength = 1)] public string DisplayName { get; set; } = "";
    [Required, StringLength(200)] public string Password { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
            yield return new ValidationResult("Name must not be blank.", [nameof(DisplayName)]);
        if (Password.Length < 8 || !Password.Any(char.IsLetter) || !Password.Any(char.IsDigit))
            yield return new ValidationResult("Password must be at least 8 characters and contain a letter and a digit.", [nameof(Password)]);
    }
}
