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

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
