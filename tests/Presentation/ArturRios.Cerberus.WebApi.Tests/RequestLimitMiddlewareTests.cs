using ArturRios.Cerberus.WebApi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

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
        if (declaredLength)
            context.Request.ContentLength = 16;
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

    // Every way a reader can pull the body: synchronous array and span reads, and both asynchronous overloads.
    public static TheoryData<string> ReadKinds => new() { "array", "span", "asyncArray", "asyncMemory" };

    private static async Task<int> ReadAll(Stream body, string kind)
    {
        var buffer = new byte[3];
        var total = 0;
        while (true)
        {
            var read = kind switch
            {
                "array" => body.Read(buffer, 0, buffer.Length),
                "span" => body.Read(buffer.AsSpan()),
                "asyncArray" => await body.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None),
                _ => await body.ReadAsync(buffer.AsMemory(), CancellationToken.None)
            };
            if (read == 0)
                return total;
            total += read;
        }
    }

    [UnitTheory]
    [MemberData(nameof(ReadKinds))]
    public async Task GivenBodyAtLimit_WhenReadingInChunksThroughEachReadOverload_ThenReadEveryByte(string kind)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[8]);
        var total = -1;
        await new RequestLimitMiddleware(async request => total = await ReadAll(request.Request.Body, kind), 8).InvokeAsync(context);

        Assert.Equal(8, total);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [UnitTheory]
    [MemberData(nameof(ReadKinds))]
    public async Task GivenUndeclaredBodyOverLimit_WhenReadingThroughEachReadOverload_ThenReturnPayloadTooLargeBeforeTheRest(string kind)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[16]);
        var finished = false;
        await new RequestLimitMiddleware(async request =>
        {
            await ReadAll(request.Request.Body, kind);
            finished = true;
        }, 8).InvokeAsync(context);

        Assert.Equal(413, context.Response.StatusCode);
        Assert.False(finished);
    }

    [UnitFact]
    public async Task GivenBoundedBody_WhenUsingItAsAnythingButAForwardOnlyReader_ThenRefuse()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[8]);
        Stream? body = null;
        await new RequestLimitMiddleware(request =>
        {
            body = request.Request.Body;
            return Task.CompletedTask;
        }, 8).InvokeAsync(context);

        Assert.NotNull(body);
        Assert.True(body.CanRead);
        Assert.False(body.CanSeek);
        Assert.False(body.CanWrite);
        Assert.Throws<NotSupportedException>(() => body.Length);
        Assert.Throws<NotSupportedException>(() => body.Position);
        Assert.Throws<NotSupportedException>(() => body.Position = 0);
        Assert.Throws<NotSupportedException>(() => body.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => body.SetLength(0));
        Assert.Throws<NotSupportedException>(() => body.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(body.Flush);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenAnyOutcome_WhenTheNextMiddlewareReturns_ThenRestoreTheOriginalBody(bool oversized)
    {
        var context = new DefaultHttpContext();
        var original = new MemoryStream(new byte[oversized ? 16 : 8]);
        context.Request.Body = original;
        await new RequestLimitMiddleware(request => request.Request.Body.CopyToAsync(Stream.Null), 8).InvokeAsync(context);

        Assert.Same(original, context.Request.Body);
        Assert.Equal(oversized ? 413 : 200, context.Response.StatusCode);
    }

    [UnitFact]
    public async Task GivenResponseAlreadyStarted_WhenTheBodyExceedsTheLimit_ThenPropagateInsteadOfRewritingTheStatus()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponse());
        var original = new MemoryStream(new byte[16]);
        context.Request.Body = original;
        var middleware = new RequestLimitMiddleware(request => request.Request.Body.CopyToAsync(Stream.Null), 8);

        var exception = await Assert.ThrowsAsync<BadHttpRequestException>(() => middleware.InvokeAsync(context));

        Assert.Equal(413, exception.StatusCode);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Same(original, context.Request.Body);
    }

    private sealed class StartedResponse : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
