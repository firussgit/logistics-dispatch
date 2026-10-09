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

public enum UserRole
{
    Customer,
    Dispatcher,
    Driver
}

public enum DriverStatus
{
    Idle,
    Busy,
    Offline
}
