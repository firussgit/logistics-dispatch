namespace LogisticsDispatch.Core.Abstractions;

public interface IJobRepository
{
    void Add(Job job);
    Task<Job?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Job?> GetWithHistoryAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> ListAsync(JobStatus? status = null, CancellationToken ct = default);
}

public interface IDriverRepository
{
    Task<Driver?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Driver>> ListAsync(CancellationToken ct = default);
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
}
