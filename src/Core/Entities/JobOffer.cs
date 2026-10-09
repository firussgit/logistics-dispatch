namespace LogisticsDispatch.Core.Entities;

/// <summary>A time-limited proposal to a single driver to take a job.</summary>
public class JobOffer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Guid DriverId { get; set; }
    public OfferStatus Status { get; set; } = OfferStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }

    /// <summary>Optimistic concurrency token, bumped by the DbContext on every update.</summary>
    public long Version { get; set; }

    public static JobOffer Create(Guid jobId, Guid driverId, DateTimeOffset now, TimeSpan ttl) =>
        new() { JobId = jobId, DriverId = driverId, CreatedAt = now, ExpiresAt = now + ttl };

    public void Accept(DateTimeOffset now)
    {
        EnsureActive(now);
        Close(OfferStatus.Accepted, now);
    }

    public void Decline(DateTimeOffset now)
    {
        EnsureActive(now);
        Close(OfferStatus.Declined, now);
    }

    /// <summary>Marks a still-pending offer as timed out. No-op if it was already answered.</summary>
    public bool Expire(DateTimeOffset now)
    {
        if (Status != OfferStatus.Pending) return false;
        Close(OfferStatus.Expired, now);
        return true;
    }

    /// <summary>Withdraws a still-pending offer (job taken, cancelled, or assigned elsewhere).</summary>
    public bool Cancel(DateTimeOffset now)
    {
        if (Status != OfferStatus.Pending) return false;
        Close(OfferStatus.Cancelled, now);
        return true;
    }

    private void EnsureActive(DateTimeOffset now)
    {
        if (Status != OfferStatus.Pending) throw new OfferNotActiveException(Id, $"it is {Status}");
        if (now >= ExpiresAt) throw new OfferNotActiveException(Id, "it has expired");
    }

    private void Close(OfferStatus status, DateTimeOffset now)
    {
        Status = status;
        RespondedAt = now;
    }
}
