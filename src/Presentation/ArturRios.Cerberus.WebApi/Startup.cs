using ArturRios.Cerberus.Data;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.WebApi.Configuration;
using ArturRios.Cerberus.WebApi.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

namespace ArturRios.Cerberus.WebApi;

public static class Startup
{
    public static WebApplication CreateApplication(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = CerberusConfiguration.Load(builder.Configuration);
        var result = new CerberusOptionsValidator(builder.Environment.IsProduction()).Validate(null, options);
        if (result.Failed) throw new OptionsValidationException(nameof(CerberusOptions), typeof(CerberusOptions), result.Failures);

        // Do not load arbitrary logging enrichers/sinks from request-controlled configuration.
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(configuration => configuration.MinimumLevel.Warning().WriteTo.Console());
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<HeimdallTokenValidator>();
        builder.Services.AddDbContext<AppDbContext>(configuration => configuration.UseNpgsql(options.ConnectionString));
        var ledger = new FileErasureLedger(options.ErasureLedgerPath!);
        builder.Services.AddSingleton<IErasureLedger>(ledger);

        var application = builder.Build();
        application.UseMiddleware<NoStoreMiddleware>();
        application.UseMiddleware<RequestLimitMiddleware>(options.MaxRequestBytes);
        // No business routes are exposed until their reviewed use-case implementation exists.
        return application;
    }
}
