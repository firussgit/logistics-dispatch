using LogisticsDispatch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LogisticsDispatch.Infrastructure.Repositories;

public class JobRepository(DispatchDbContext db) : IJobRepository
{
    public void Add(Job job) => db.Jobs.Add(job);

    public Task<Job?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct);

    public Task<Job?> GetWithHistoryAsync(Guid id, CancellationToken ct = default) =>
        db.Jobs.Include(j => j.History).FirstOrDefaultAsync(j => j.Id == id, ct);

    public async Task<IReadOnlyList<Job>> ListAsync(JobStatus? status = null, CancellationToken ct = default)
    {
        var query = db.Jobs.AsQueryable();
        if (status is { } s) query = query.Where(j => j.Status == s);
        return await query.OrderByDescending(j => j.CreatedAt).ToListAsync(ct);
    }
}

public class DriverRepository(DispatchDbContext db) : IDriverRepository
{
    public Task<Driver?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Drivers.FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<Driver>> ListAsync(CancellationToken ct = default) =>
        await db.Drivers.OrderBy(d => d.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<Driver>> GetIdleAsync(CancellationToken ct = default) =>
        await db.Drivers.Where(d => d.Status == DriverStatus.Idle).ToListAsync(ct);
}

public class UnitOfWork(DispatchDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The record was modified by another request. Reload and retry.", ex);
        }
    }
}
