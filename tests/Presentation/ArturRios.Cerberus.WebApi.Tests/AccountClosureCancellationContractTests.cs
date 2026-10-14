using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;

namespace ArturRios.Cerberus.WebApi.Tests;

public class AccountClosureCancellationContractTests
{
    [UnitFact]
    public void GivenRecoveryMfaChallenge_WhenGeneratingContract_ThenPermitNullAccountContext()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ApplicationName=typeof(Startup).Assembly.GetName().Name,Args=[]});
        builder.Services.AddCerberusContracts();using var host=builder.Build();var doc=host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var account=doc.Components!.Schemas!["ClosureRecoveryAuthenticationOutput"].Properties!["account"];
        Assert.Contains(account.OneOf!,x=>x.Enum?.Count==1&&x.Enum[0] is null);
    }
    [UnitTheory][InlineData("/api/accounts/me/closure/cancel")][InlineData("/api/auth/closure/login")][InlineData("/api/auth/closure/2fa/verify")]
    public void GivenRestrictedLifecycleContract_WhenGenerating_ThenRequireStrictJsonAndEmpty413(string path)
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ApplicationName=typeof(Startup).Assembly.GetName().Name,Args=[]});
        builder.Services.AddCerberusContracts();using var host=builder.Build();var doc=host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var op=doc.Paths[path].Operations![HttpMethod.Post];Assert.Equal("application/json",Assert.Single(op.RequestBody!.Content!).Key);Assert.True(op.RequestBody.Required);
        Assert.Empty(op.Responses!["413"].Content!);Assert.True(op.Responses.ContainsKey("410"));
    }
}
