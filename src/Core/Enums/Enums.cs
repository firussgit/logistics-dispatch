namespace LogisticsDispatch.Core.Enums;

public enum JobStatus
{
    Pending,
    Assigned,
    InTransit,
    Completed,
    Cancelled
}

public enum OfferStatus
{
    Pending,
    Accepted,
    Declined,
    Expired,
    Cancelled
}

public enum DriverStatus
{
    Idle,
    Busy,
    Offline
}
