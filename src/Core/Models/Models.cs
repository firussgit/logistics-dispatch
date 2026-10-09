namespace LogisticsDispatch.Core.Models;

public record StatusHistoryDto(JobStatus From, JobStatus To, DateTimeOffset At, string? Note);

public record JobDto(
    Guid Id,
    string Reference,
    string CustomerName,
    string? Notes,
    Location Pickup,
    Location Dropoff,
    JobStatus Status,
    Guid? DriverId,
    Location CurrentLocation,
    int? EtaSeconds,
    double Progress,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<StatusHistoryDto>? History = null)
{
    public static JobDto From(Job j, bool includeHistory = false) => new(
        j.Id, j.Reference, j.CustomerName, j.Notes, j.Pickup, j.Dropoff, j.Status, j.DriverId,
        j.CurrentLocation, j.EtaSeconds, j.Progress, j.CreatedAt, j.UpdatedAt,
        includeHistory
            ? j.History.OrderBy(h => h.At).Select(h => new StatusHistoryDto(h.From, h.To, h.At, h.Note)).ToList()
            : null);
}

public record DriverDto(Guid Id, string Name, DriverStatus Status, Location CurrentLocation, Guid? ActiveJobId, bool IsAutomated)
{
    public static DriverDto From(Driver d) => new(d.Id, d.Name, d.Status, d.CurrentLocation, d.ActiveJobId, d.IsAutomated);
}

public record JobStatusChangedEvent(Guid JobId, string Reference, JobStatus Status, Guid? DriverId, DateTimeOffset At);

public record JobProgressEvent(Guid JobId, double Lat, double Lng, int? EtaSeconds, double Progress);

public record SystemStatusDto(
    IReadOnlyDictionary<string, int> JobsByStatus,
    int IdleDrivers,
    int BusyDrivers,
    int OfflineDrivers,
    double? AverageEtaSeconds);

/// <summary>An offer plus just enough job context for a driver to decide.</summary>
public record OfferDto(
    Guid Id,
    Guid JobId,
    string Reference,
    string CustomerName,
    Guid DriverId,
    OfferStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    Location Pickup,
    Location Dropoff,
    double DistanceToPickupMeters,
    double TripMeters,
    decimal Payout)
{
    public static OfferDto From(JobOffer o, Job job, Driver driver)
    {
        var toPickup = GeoMath.DistanceMeters(driver.CurrentLocation, job.Pickup);
        var trip = GeoMath.DistanceMeters(job.Pickup, job.Dropoff);
        return new OfferDto(o.Id, o.JobId, job.Reference, job.CustomerName, o.DriverId, o.Status, o.CreatedAt, o.ExpiresAt,
            job.Pickup, job.Dropoff, toPickup, trip, CalculatePayout(toPickup, trip));
    }

    /// <summary>Simple demo pay model: base fare + per-km for the trip + a smaller per-km for the drive to pickup.</summary>
    public static decimal CalculatePayout(double toPickupMeters, double tripMeters) =>
        Math.Round(3m + 1.2m * (decimal)(tripMeters / 1000) + 0.4m * (decimal)(toPickupMeters / 1000), 2);
}
