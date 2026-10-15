using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;
namespace ArturRios.Cerberus.WebApi.Tests;

public class AccountDeletionContractTests
{
    [UnitFact]
    public void GivenPermanentDeletionContract_WhenGenerating_ThenRequireStrictJsonAndDescribeCompletedOrTrackedState()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ApplicationName=typeof(Startup).Assembly.GetName().Name,Args=[]});builder.Services.AddCerberusContracts();using var host=builder.Build();var doc=host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var op=doc.Paths["/api/accounts/me/permanent"].Operations![HttpMethod.Delete];Assert.Equal("application/json",Assert.Single(op.RequestBody!.Content!).Key);Assert.True(op.RequestBody.Required);Assert.Empty(op.Responses!["413"].Content!);Assert.True(op.Responses.ContainsKey("202"));
        Assert.Equal(2,doc.Components!.Schemas!["AccountDeletionOutput"].Properties!["state"].Enum!.Count);
        Assert.Equal(new[]{"accountId","deletedAt","state"}.Order(),doc.Components!.Schemas!["AccountDeletionOutput"].Required!.Order());
    }
}
