using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.BuildingBlocks.Common;

/// <summary>Business-rule failure that should be returned to the client with a specific HTTP status.</summary>
public class AppException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public class NotFoundException(string message) : AppException(message, StatusCodes.Status404NotFound);
public class ConflictException(string message) : AppException(message, StatusCodes.Status409Conflict);
public class ForbiddenException(string message = "You are not allowed to perform this action.")
    : AppException(message, StatusCodes.Status403Forbidden);

/// <summary>Converts exceptions into RFC 7807 ProblemDetails responses.</summary>
public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problemDetails)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        // The client went away (navigated off the page, Angular cancelled the request): not an error.
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Request {Path} was cancelled by the client", context.Request.Path);
            context.Response.StatusCode = 499;   // "client closed request" - nobody is listening for a body
            return true;
        }

        var (status, title, detail) = exception switch
        {
            AppException app => (app.StatusCode, "Request could not be completed", app.Message),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error",
                  "An unexpected error occurred. Please try again or contact support.")
        };

        if (status >= 500) logger.LogError(exception, "Unhandled exception for {Path}", context.Request.Path);

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail }
        });
    }
}
