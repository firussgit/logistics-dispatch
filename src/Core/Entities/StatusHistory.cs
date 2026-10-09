namespace LogisticsDispatch.Core.Entities;

public class StatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public JobStatus From { get; set; }
    public JobStatus To { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Note { get; set; }
}
