namespace LogisticsDispatch.Core.Abstractions;

public interface IJobRepository
{
    void Add(Job job);
    Task<Job?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Job?> GetWithHistoryAsync(Guid id, CancellationToken ct = default);
    Task<Job?> GetByTrackingTokenAsync(string token, CancellationToken ct = default);
    /// <summary>Jobs visible to one customer and/or one driver (newest first).</summary>
    Task<IReadOnlyList<Job>> ListForUserAsync(JobStatus? status, Guid? customerId, Guid? driverId, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> ListAsync(JobStatus? status = null, CancellationToken ct = default);
}

public interface IDriverRepository
{
    Task<Driver?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Driver>> ListAsync(CancellationToken ct = default);
    void Add(Driver driver);
    Task<IReadOnlyList<Driver>> GetIdleAsync(CancellationToken ct = default);
}

public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyConflictException">A concurrent writer changed the same row.</exception>
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Pushes real-time updates to connected clients. Called only after changes are persisted.</summary>
public interface IDispatchNotifier
{
    Task JobCreatedAsync(JobDto job, CancellationToken ct = default);
    Task JobStatusChangedAsync(JobStatusChangedEvent e, CancellationToken ct = default);
    Task JobProgressAsync(JobProgressEvent e, CancellationToken ct = default);
    Task DriverUpdatedAsync(DriverDto driver, CancellationToken ct = default);
    Task OfferCreatedAsync(OfferDto offer, CancellationToken ct = default);
    Task OfferUpdatedAsync(OfferDto offer, CancellationToken ct = default);
    Task RouteReadyAsync(RouteReadyEvent e, CancellationToken ct = default);
}

public interface IOfferRepository
{
    void Add(JobOffer offer);
    Task<JobOffer?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<JobOffer>> ListAsync(OfferStatus? status = null, Guid? driverId = null, CancellationToken ct = default);
    Task<IReadOnlyList<JobOffer>> ListForJobAsync(Guid jobId, CancellationToken ct = default);
    /// <summary>Every offer created at or after <paramref name="since"/> (used for cooldown/expiry sweeps).</summary>
    Task<IReadOnlyList<JobOffer>> ListSinceAsync(DateTimeOffset since, CancellationToken ct = default);
}

public interface IUserRepository
{
    void Add(User user);
    Task<User?> GetAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListByRoleAsync(UserRole role, CancellationToken ct = default);
}
