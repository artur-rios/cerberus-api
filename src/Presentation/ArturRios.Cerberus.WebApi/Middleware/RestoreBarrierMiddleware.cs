using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Output;

namespace ArturRios.Cerberus.WebApi.Middleware;

public sealed class RestoreBarrierMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RestoreTrafficGate gate)
    {
        if (gate.IsReady) { await next(context); return; }
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(ProcessOutput.New.WithError("restore_reconciliation_required"), context.RequestAborted);
    }
}
