using ArturRios.Output;
using Serilog.Context;

namespace ArturRios.Cerberus.WebApi.Middleware;

public sealed class SafeFailureMiddleware(RequestDelegate next, ILogger<SafeFailureMiddleware> logger)
{
    // A request body Kestrel refuses while the API reads it is the client's mistake, not a server failure:
    // Kestrel throws a BadHttpRequestException carrying the status it means, which is answered with that
    // status and a code of its own. RequestLimitMiddleware answers Kestrel's 413 (its body limit) first, with
    // no body, so only these two reach here; any other BadHttpRequestException is a failure like any other.
    public static readonly IReadOnlyDictionary<int, string> BodyRefusals = new Dictionary<int, string>
    {
        [StatusCodes.Status400BadRequest] = "request_body_invalid",
        [StatusCodes.Status408RequestTimeout] = "request_timeout"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        context.TraceIdentifier = Guid.NewGuid().ToString("N");
        using var correlation = LogContext.PushProperty("CorrelationId", context.TraceIdentifier);
        try
        { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (BadHttpRequestException exception) when (BodyRefusals.ContainsKey(exception.StatusCode))
        {
            var code = BodyRefusals[exception.StatusCode];
            logger.LogInformation("Request refused. Code={Code}", code);
            await Answer(context, exception.StatusCode, code);
        }
        catch (Exception)
        {
            logger.LogWarning("Request failed. Code={Code}", "internal_failure");
            await Answer(context, StatusCodes.Status500InternalServerError, "internal_failure");
        }
        finally { logger.LogInformation("Request completed. Code={Code}; Status={Status}", "request_completed", context.Response.StatusCode); }
    }

    private static async Task Answer(HttpContext context, int status, string code)
    {
        if (context.Response.HasStarted)
        { context.Abort(); return; }
        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(ProcessOutput.New.WithError(code), context.RequestAborted);
    }
}
