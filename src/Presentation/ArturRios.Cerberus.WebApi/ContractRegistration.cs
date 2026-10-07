using ArturRios.Output;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace ArturRios.Cerberus.WebApi;

public static class ContractRegistration
{
    public static IServiceCollection AddCerberusContracts(this IServiceCollection services)
    {
        services.AddControllers().AddApplicationPart(typeof(Program).Assembly)
            .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = _ =>
                new BadRequestObjectResult(ProcessOutput.New.WithError("validation_failed")));
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Cerberus API",
            Version = "v1",
            Description = "Foundation only. Business endpoints are added by their reviewed use cases."
        }));
        return services;
    }
}
