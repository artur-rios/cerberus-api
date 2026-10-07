using ArturRios.Output;
using Serilog.Context;

namespace ArturRios.Cerberus.WebApi.Middleware;

public sealed class SafeFailureMiddleware(RequestDelegate next, ILogger<SafeFailureMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.TraceIdentifier = Guid.NewGuid().ToString("N");
        using var correlation = LogContext.PushProperty("CorrelationId", context.TraceIdentifier);
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            logger.LogWarning("Request failed. Code={Code}", "internal_failure");
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(ProcessOutput.New.WithError("internal_failure"), context.RequestAborted);
        }
        finally { logger.LogInformation("Request completed. Code={Code}; Status={Status}", "request_completed", context.Response.StatusCode); }
    }
}
