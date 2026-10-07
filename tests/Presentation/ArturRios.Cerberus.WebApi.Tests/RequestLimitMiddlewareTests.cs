using ArturRios.Cerberus.WebApi.Middleware;
using Microsoft.AspNetCore.Http;

namespace ArturRios.Cerberus.WebApi.Tests;

public class RequestLimitMiddlewareTests
{
    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GivenOversizedBody_WhenReadingRequest_ThenReturnPayloadTooLarge(bool declaredLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[16]);
        context.Response.Body = new MemoryStream();
        if (declaredLength) context.Request.ContentLength = 16;
        var consumed = false;
        await new RequestLimitMiddleware(async request =>
        {
            await request.Request.Body.CopyToAsync(Stream.Null);
            consumed = true;
        }, 8).InvokeAsync(context);

        Assert.Equal(413, context.Response.StatusCode);
        Assert.False(consumed);
    }

    [UnitFact]
    public async Task GivenBodyAtLimit_WhenReadingRequest_ThenAccept()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[8]);
        var output = new MemoryStream();
        await new RequestLimitMiddleware(request => request.Request.Body.CopyToAsync(output), 8).InvokeAsync(context);

        Assert.Equal(8, output.Length);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [UnitFact]
    public async Task GivenMaximumRepresentableLimit_WhenReadingSmallBody_ThenAcceptWithoutOverflow()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[8]);
        var output = new MemoryStream();
        await new RequestLimitMiddleware(request => request.Request.Body.CopyToAsync(output), long.MaxValue).InvokeAsync(context);
        Assert.Equal(8, output.Length);
    }
}
