using LogisticsDispatch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LogisticsDispatch.Infrastructure.Repositories;

public class JobRepository(DispatchDbContext db) : IJobRepository
{
    public void Add(Job job) => db.Jobs.Add(job);

    public Task<Job?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct);

    public Task<Job?> GetByTrackingTokenAsync(string token, CancellationToken ct = default) =>
        db.Jobs.Include(j => j.History).FirstOrDefaultAsync(j => j.TrackingToken == token, ct);

    public async Task<IReadOnlyList<Job>> ListForUserAsync(JobStatus? status, Guid? customerId, Guid? driverId, CancellationToken ct = default)
    {
        var query = db.Jobs.AsQueryable();
        if (status is { } s) query = query.Where(j => j.Status == s);
        if (customerId is { } c) query = query.Where(j => j.CustomerId == c);
        if (driverId is { } d) query = query.Where(j => j.DriverId == d);
        return await query.OrderByDescending(j => j.CreatedAt).ToListAsync(ct);
    }

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
    public void Add(Driver driver) => db.Drivers.Add(driver);

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

public class OfferRepository(DispatchDbContext db) : IOfferRepository
{
    public void Add(JobOffer offer) => db.Offers.Add(offer);

    public Task<JobOffer?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Offers.FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<IReadOnlyList<JobOffer>> ListAsync(OfferStatus? status = null, Guid? driverId = null, CancellationToken ct = default)
    {
        var query = db.Offers.AsQueryable();
        if (status is { } s) query = query.Where(o => o.Status == s);
        if (driverId is { } d) query = query.Where(o => o.DriverId == d);
        return await query.OrderByDescending(o => o.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<JobOffer>> ListForJobAsync(Guid jobId, CancellationToken ct = default) =>
        await db.Offers.Where(o => o.JobId == jobId).OrderBy(o => o.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<JobOffer>> ListSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
        await db.Offers.Where(o => o.CreatedAt >= since).OrderBy(o => o.CreatedAt).ToListAsync(ct);
}

public class UserRepository(DispatchDbContext db) : IUserRepository
{
    public void Add(User user) => db.Users.Add(user);

    public Task<User?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

    public async Task<IReadOnlyList<User>> ListByRoleAsync(UserRole role, CancellationToken ct = default) =>
        await db.Users.Where(u => u.Role == role).OrderBy(u => u.DisplayName).ToListAsync(ct);
}
