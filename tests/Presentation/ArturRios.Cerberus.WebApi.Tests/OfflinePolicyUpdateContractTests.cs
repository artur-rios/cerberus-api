using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;

namespace ArturRios.Cerberus.WebApi.Tests;

public class OfflinePolicyUpdateContractTests
{
    [UnitFact]
    public void GivenStrictPolicyUpdate_WhenGeneratingContract_ThenRequireJsonBodyAndAdvertiseEmpty413()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Startup).Assembly.GetName().Name,
            Args = []
        });
        builder.Services.AddCerberusContracts();
        using var host = builder.Build();
        var document = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var operation = document.Paths["/api/accounts/me/offline-policy"].Operations![HttpMethod.Put];
        Assert.Equal("application/json", Assert.Single(operation.RequestBody!.Content!).Key);
        Assert.True(operation.RequestBody.Required);
        Assert.Empty(operation.Responses!["413"].Content!);
    }
}
