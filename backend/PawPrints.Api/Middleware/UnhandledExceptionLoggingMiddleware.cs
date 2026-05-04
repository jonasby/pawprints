namespace PawPrints.Api.Middleware;

/// <summary>
/// Logs unhandled exceptions with request context before rethrowing so host-level handlers can still run.
/// </summary>
public sealed class UnhandledExceptionLoggingMiddleware(RequestDelegate next, ILogger<UnhandledExceptionLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unhandled exception while processing HTTP {Method} {Path}",
                context.Request.Method,
                context.Request.Path.Value ?? string.Empty
            );
            throw;
        }
    }
}
