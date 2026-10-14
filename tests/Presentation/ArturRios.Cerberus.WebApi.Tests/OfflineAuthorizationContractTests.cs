using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;

namespace ArturRios.Cerberus.WebApi.Tests;

public class OfflineAuthorizationContractTests
{
    [UnitFact]
    public void GivenStrictRenewalRoute_WhenGeneratingOpenApi_ThenRequireJsonAndAdvertiseEmpty413()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(Startup).Assembly.GetName().Name, Args = [] });
        builder.Services.AddCerberusContracts(); using var host = builder.Build();
        var doc = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var operation = doc.Paths["/api/offline-authorizations"].Operations![HttpMethod.Post];
        Assert.True(operation.RequestBody!.Required);
        Assert.Equal("application/json", Assert.Single(operation.RequestBody.Content!).Key);
        Assert.Empty(operation.Responses!["413"].Content!);
    }
}
