using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;

namespace ArturRios.Cerberus.WebApi.Tests;

public class AccountClosureContractTests
{
    [UnitFact]
    public void GivenClosureContract_WhenGenerating_ThenRequireStrictJsonAndDocumentEveryError()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions {ApplicationName=typeof(Startup).Assembly.GetName().Name,Args=[]});
        builder.Services.AddCerberusContracts();using var host=builder.Build();
        var document=host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var operation=document.Paths["/api/accounts/me/closure"].Operations![HttpMethod.Post];
        Assert.Equal("application/json",Assert.Single(operation.RequestBody!.Content!).Key);Assert.True(operation.RequestBody.Required);
        Assert.Empty(operation.Responses!["413"].Content!);
        foreach(var status in new[]{"200","400","401","404","409","503"})Assert.True(operation.Responses.ContainsKey(status));
    }
}
