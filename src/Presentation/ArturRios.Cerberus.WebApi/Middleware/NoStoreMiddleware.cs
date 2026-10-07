using Microsoft.AspNetCore.Http;

namespace ArturRios.Cerberus.WebApi.Middleware;

public sealed class NoStoreMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        Apply(context.Response);
        context.Response.OnStarting(() => { Apply(context.Response); return Task.CompletedTask; });
        return next(context);
    }

    private static void Apply(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Remove("ETag");
        response.Headers.Remove("Last-Modified");
    }
}
