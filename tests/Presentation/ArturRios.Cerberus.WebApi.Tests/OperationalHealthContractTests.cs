using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
namespace ArturRios.Cerberus.WebApi.Tests;

public sealed class OperationalHealthContractTests
{
    [UnitFact]
    public void GivenHealthContract_WhenGeneratingOpenApi_ThenClosedRequiredShapesAndSafeErrorFamilies()
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(Startup).Assembly.GetName().Name, Args = [] });
        b.Services.AddCerberusContracts();
        using var host = b.Build();
        var doc = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        foreach (var path in new[] { "/health/live", "/health/ready", "/health/details" })
            Assert.Contains(path, doc.Paths.Keys);
        var schemas = doc.Components!.Schemas!;
        Assert.Equal(["status"], schemas["HealthStatus"].Required);
        Assert.False(schemas["HealthStatus"].AdditionalPropertiesAllowed);
        Assert.Equal(["components", "status"], schemas["HealthDetails"].Required!.Order());
        Assert.Equal(["elapsedMilliseconds", "name", "status"], schemas["HealthComponent"].Required!.Order());
        Assert.False(schemas["HealthComponent"].AdditionalPropertiesAllowed);
        var details = doc.Paths["/health/details"].Operations![HttpMethod.Get];
        // 413 is the request limit's and 500 SafeFailureMiddleware's, both before routing.
        Assert.Equal(["200", "401", "403", "413", "500", "503"], details.Responses!.Keys.Order());
        foreach (var code in new[] { "401", "403" })
            Assert.Equal("ProcessOutput", Assert.IsType<OpenApiSchemaReference>(details.Responses[code].Content!["application/json"].Schema!).Reference.Id);
        Assert.NotEmpty(details.Responses["503"].Content!["application/json"].Schema!.OneOf!);
    }
}
