using ArturRios.Cerberus.WebApi.Middleware;
using Microsoft.AspNetCore.Http;

namespace ArturRios.Cerberus.WebApi.Tests;

public class NoStoreMiddlewareTests
{
    [UnitFact]
    public async Task GivenSensitiveResponseWithValidators_WhenPipelineRuns_ThenPreventCaching()
    {
        var context = new DefaultHttpContext();
        context.Response.Headers.ETag = "\"secret-revision\"";
        context.Response.Headers.LastModified = "Wed, 07 Oct 2026 00:00:00 GMT";
        await new NoStoreMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.False(context.Response.Headers.ContainsKey("Last-Modified"));
    }
}
