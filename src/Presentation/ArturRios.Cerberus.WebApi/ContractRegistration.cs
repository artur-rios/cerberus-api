using ArturRios.Output;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace ArturRios.Cerberus.WebApi;

public static class ContractRegistration
{
    public static IServiceCollection AddCerberusContracts(this IServiceCollection services)
    {
        services.AddControllers().AddApplicationPart(typeof(Program).Assembly)
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.AllowDuplicateProperties = false;
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
                options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
                options.JsonSerializerOptions.Converters.Add(new CanonicalGuidConverter());
            })
            .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = _ =>
                new BadRequestObjectResult(ProcessOutput.New.WithError("validation_failed")));
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Cerberus API",
            Version = "v1",
            Description = "Cerberus encrypted vault API. Identity authentication and vault access are separate."
        }));
        return services;
    }
}
