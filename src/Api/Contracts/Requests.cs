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
    [Required, StringLength(100, MinimumLength = 1)]
    public string CustomerName { get; set; } = "";

    [StringLength(500)]
    public string? Notes { get; set; }

    [Required] public LocationInput? Pickup { get; set; }
    [Required] public LocationInput? Dropoff { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(CustomerName))
            yield return new ValidationResult("CustomerName must not be blank.", [nameof(CustomerName)]);
        if (Pickup is not null && Dropoff is not null && Pickup.Lat == Dropoff.Lat && Pickup.Lng == Dropoff.Lng)
            yield return new ValidationResult("Pickup and dropoff must differ.", [nameof(Dropoff)]);
    }
}

public class AssignJobRequest
{
    [Required] public Guid? DriverId { get; set; }
}
