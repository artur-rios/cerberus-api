using ArturRios.Output;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using ArturRios.Cerberus.WebApi.Controllers;
using Swashbuckle.AspNetCore.SwaggerGen;

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
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Cerberus API",
                Version = "v1",
                Description = "Cerberus encrypted vault API. Identity authentication and vault access are separate."
            });
            options.OperationFilter<StrictRequestContentFilter>();
        });
        return services;
    }

    // Documentation only: runtime rejects unsupported media with 400, not MVC's 415.
    private sealed class StrictRequestContentFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var policyUpdate = context.MethodInfo.DeclaringType == typeof(AccountController)
                && context.MethodInfo.Name == nameof(AccountController.SetOfflinePolicy);
            var renewal = context.MethodInfo.DeclaringType == typeof(OfflineAuthorizationsController)
                && context.MethodInfo.Name == nameof(OfflineAuthorizationsController.Renew);
            if (!policyUpdate && !renewal && (context.MethodInfo.DeclaringType != typeof(SoftwareGrantsController)
                || context.MethodInfo.Name is not (nameof(SoftwareGrantsController.GrantAccess)
                    or nameof(SoftwareGrantsController.RevokeAccess)))) return;
            var content = operation.RequestBody!.Content!;
            var json = content["application/json"];
            content.Clear();
            content.Add("application/json", json);
            if (policyUpdate || renewal || context.MethodInfo.Name == nameof(SoftwareGrantsController.RevokeAccess))
            {
                ((OpenApiRequestBody)operation.RequestBody!).Required = true;
                operation.Responses!["413"].Content?.Clear();
            }
            if (context.MethodInfo.Name == nameof(SoftwareGrantsController.RevokeAccess))
            {
                var id = operation.Parameters!.OfType<OpenApiParameter>().Single(p => p.Name == "id" && p.In == ParameterLocation.Path);
                id.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" };
            }
        }
    }
}
