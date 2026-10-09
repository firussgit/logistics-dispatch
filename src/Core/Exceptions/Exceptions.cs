namespace LogisticsDispatch.Core.Exceptions;

public class InvalidJobTransitionException(JobStatus from, string attempted)
    : Exception($"Cannot {attempted} a job that is {from}.")
{
    public JobStatus From { get; } = from;
    public string Attempted { get; } = attempted;
}

public class DriverUnavailableException(Guid driverId, DriverStatus status)
    : Exception($"Driver {driverId} is {status} and cannot take a job.");

public class ConcurrencyConflictException(string message, Exception? inner = null)
    : Exception(message, inner);

public class NotFoundException(string entity, Guid id)
    : Exception($"{entity} {id} was not found.");
