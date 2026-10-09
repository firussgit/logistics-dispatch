using LogisticsDispatch.Core.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace LogisticsDispatch.Api.Middleware;

/// <summary>Maps domain exceptions to RFC 7807 ProblemDetails responses.</summary>
public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var (status, title) = ex switch
            {
                NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
                InvalidJobTransitionException => (StatusCodes.Status409Conflict, "Invalid state transition"),
                DriverUnavailableException => (StatusCodes.Status409Conflict, "Driver unavailable"),
                OfferNotActiveException => (StatusCodes.Status409Conflict, "Offer no longer active"),
                ConcurrencyConflictException => (StatusCodes.Status409Conflict, "Concurrency conflict"),
                _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
            };

            if (status == StatusCodes.Status500InternalServerError)
                logger.LogError(ex, "Unhandled exception");

            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = status == StatusCodes.Status500InternalServerError ? "An unexpected error occurred." : ex.Message
            });
        }
    }
}
