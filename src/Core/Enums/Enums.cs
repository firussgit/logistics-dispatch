namespace LogisticsDispatch.Core.Enums;

public enum JobStatus
{
    Pending,
    Assigned,
    InTransit,
    Completed,
    Cancelled
}

public enum DriverStatus
{
    Idle,
    Busy,
    Offline
}
