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

public record DriverDto(Guid Id, string Name, DriverStatus Status, Location CurrentLocation, Guid? ActiveJobId)
{
    public static DriverDto From(Driver d) => new(d.Id, d.Name, d.Status, d.CurrentLocation, d.ActiveJobId);
}

public record JobStatusChangedEvent(Guid JobId, string Reference, JobStatus Status, Guid? DriverId, DateTimeOffset At);

public record JobProgressEvent(Guid JobId, double Lat, double Lng, int? EtaSeconds, double Progress);

public record SystemStatusDto(
    IReadOnlyDictionary<string, int> JobsByStatus,
    int IdleDrivers,
    int BusyDrivers,
    int OfflineDrivers,
    double? AverageEtaSeconds);
