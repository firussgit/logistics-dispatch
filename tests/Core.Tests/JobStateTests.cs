using LogisticsDispatch.Core.Entities;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Exceptions;

namespace LogisticsDispatch.Core.Tests;

public class JobStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid DriverId = Guid.NewGuid();

    internal static Job JobIn(JobStatus status)
    {
        var job = Job.Create("Acme", null, new Location(40.0, -74.0), new Location(40.1, -74.1), Now);
        switch (status)
        {
            case JobStatus.Assigned: job.Assign(DriverId, Now); break;
            case JobStatus.InTransit: job.Assign(DriverId, Now); job.StartTransit(Now); break;
            case JobStatus.Completed: job.Assign(DriverId, Now); job.StartTransit(Now); job.Complete(Now); break;
            case JobStatus.Cancelled: job.Cancel(Now); break;
        }
        Assert.Equal(status, job.Status);
        return job;
    }

    private static readonly Dictionary<string, Action<Job>> Actions = new()
    {
        ["assign"] = j => j.Assign(DriverId, Now),
        ["start"] = j => j.StartTransit(Now),
        ["complete"] = j => j.Complete(Now),
        ["cancel"] = j => j.Cancel(Now),
    };

    // (from, action) -> expected resulting status; absent = illegal
    private static readonly Dictionary<(JobStatus, string), JobStatus> Legal = new()
    {
        [(JobStatus.Pending, "assign")] = JobStatus.Assigned,
        [(JobStatus.Pending, "cancel")] = JobStatus.Cancelled,
        [(JobStatus.Assigned, "start")] = JobStatus.InTransit,
        [(JobStatus.Assigned, "cancel")] = JobStatus.Cancelled,
        [(JobStatus.InTransit, "complete")] = JobStatus.Completed,
    };

    public static IEnumerable<object[]> Matrix() =>
        from s in Enum.GetValues<JobStatus>()
        from a in Actions.Keys
        select new object[] { s, a };

    [Theory, MemberData(nameof(Matrix))]
    public void Transition_matrix(JobStatus from, string action)
    {
        var job = JobIn(from);
        var historyBefore = job.History.Count;

        if (Legal.TryGetValue((from, action), out var expected))
        {
            Actions[action](job);
            Assert.Equal(expected, job.Status);
            Assert.Equal(historyBefore + 1, job.History.Count);
            var last = job.History[^1];
            Assert.Equal(from, last.From);
            Assert.Equal(expected, last.To);
        }
        else
        {
            Assert.Throws<InvalidJobTransitionException>(() => Actions[action](job));
            Assert.Equal(from, job.Status);
            Assert.Equal(historyBefore, job.History.Count);
        }
    }

    [Fact]
    public void Full_lifecycle_records_each_step()
    {
        var job = JobIn(JobStatus.Completed);
        Assert.Equal(
            [JobStatus.Pending, JobStatus.Assigned, JobStatus.InTransit, JobStatus.Completed],
            job.History.Select(h => h.To));
        Assert.Equal(DriverId, job.DriverId);
        Assert.Equal(1, job.Progress);
        Assert.Equal(job.Dropoff, job.CurrentLocation);
    }

    [Fact]
    public void Cancelling_an_assigned_job_releases_the_driver_reference()
    {
        var job = JobIn(JobStatus.Assigned);
        job.Cancel(Now, "customer request");
        Assert.Null(job.DriverId);
        Assert.Equal("customer request", job.History[^1].Note);
    }
}
