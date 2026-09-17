using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace QaDocBackend.Infrastructure;

public class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        (int status, string title) = exception switch
        {
            PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } => (StatusCodes.Status409Conflict, "A record with the same unique value already exists."),
            PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } => (StatusCodes.Status409Conflict, "The operation conflicts with related records (invalid reference or record still in use)."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
        };

        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception for {Path}", context.Request.Path);

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title }, ct);
        return true;
    }
}
