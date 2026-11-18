using ArturRios.Cerberus.WebApi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace ArturRios.Cerberus.WebApi.Tests;

public sealed class SafeFailureMiddlewareTests
{
    [UnitFact]
    public async Task GivenSensitiveDependencyException_WhenHandling_ThenReturnAndLogOnlyStableCodes()
    {
        const string protectedValue = "fixture-password-token-ciphertext-personal-details";
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var factory = new SerilogLoggerFactory(logger);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.QueryString = new QueryString("?token=" + protectedValue);
        var middleware = new SafeFailureMiddleware(_ => throw new IOException(protectedValue), factory.CreateLogger<SafeFailureMiddleware>());
        await middleware.InvokeAsync(context);
        Assert.Equal(500, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("internal_failure", body);
        Assert.DoesNotContain(protectedValue, body);
        Assert.NotEmpty(sink.Events);
        Assert.All(sink.Events, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(protectedValue, entry.RenderMessage());
            Assert.True(entry.Properties.ContainsKey("CorrelationId"));
        });
    }

    [UnitTheory]
    [InlineData(400, "request_body_invalid")]
    [InlineData(408, "request_timeout")]
    public async Task GivenABodyKestrelRefuses_WhenHandling_ThenAnswerItsOwnStatusAsAClientError(int status, string code)
    {
        var sink = new CapturingSink();
        var (context, body) = await Handle(new BadHttpRequestException("fixture-request-detail", status), sink);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);
        using var json = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal([code], json.RootElement.GetProperty("errors").EnumerateArray().Select(error => error.GetString()));
        Assert.False(json.RootElement.TryGetProperty("data", out _));
        Assert.DoesNotContain("fixture-request-detail", body);
        // Logged as the client's mistake: no warning, no exception or stack trace.
        Assert.All(sink.Events, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.True(entry.Level <= LogEventLevel.Information);
        });
        Assert.Contains(sink.Events, entry => entry.RenderMessage().Contains(code));
    }

    [UnitTheory]
    [InlineData(413)]
    [InlineData(431)]
    public async Task GivenABadRequestExceptionWithAnotherStatus_WhenHandling_ThenAnswerInternalFailure(int status)
    {
        var (context, body) = await Handle(new BadHttpRequestException("fixture", status), new CapturingSink());

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Contains("internal_failure", body);
    }

    [UnitFact]
    public async Task GivenAnOrdinaryException_WhenHandling_ThenStillAnswerInternalFailure()
    {
        var sink = new CapturingSink();
        var (context, body) = await Handle(new InvalidOperationException("fixture"), sink);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);
        Assert.Contains("\"errors\":[\"internal_failure\"]", body);
        Assert.Contains(sink.Events, entry => entry.Level == LogEventLevel.Warning && entry.RenderMessage().Contains("internal_failure"));
    }

    [UnitFact]
    public async Task GivenABodyRefusalAfterTheResponseStarted_WhenHandling_ThenAbortTheConnection()
    {
        var context = new DefaultHttpContext();
        var lifetime = new AbortRecorder();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(new StartedResponse());
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature>(lifetime);
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using var factory = new SerilogLoggerFactory(logger);

        await new SafeFailureMiddleware(_ => throw new BadHttpRequestException("fixture", 408), factory.CreateLogger<SafeFailureMiddleware>())
            .InvokeAsync(context);

        Assert.True(lifetime.Aborted);
    }

    private static async Task<(DefaultHttpContext Context, string Body)> Handle(Exception exception, CapturingSink sink)
    {
        using var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var factory = new SerilogLoggerFactory(logger);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new SafeFailureMiddleware(_ => throw exception, factory.CreateLogger<SafeFailureMiddleware>()).InvokeAsync(context);
        context.Response.Body.Position = 0;
        return (context, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private sealed class StartedResponse : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class AbortRecorder : Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => Aborted = true;
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
