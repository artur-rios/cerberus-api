using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;
namespace ArturRios.Cerberus.WebApi.Tests;
public sealed class SoftwareSecretContractTests
{
    [UnitFact]
    public void GivenSoftwareReadContract_WhenGeneratingOpenApi_ThenRequireSelectionAndAllCiphertextFields()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ApplicationName=typeof(Startup).Assembly.GetName().Name,Args=[]});
        builder.Services.AddCerberusContracts();using var host=builder.Build();var doc=host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var op=Assert.Single(doc.Paths["/api/software-secrets"].Operations!).Value;
        Assert.Null(op.RequestBody);Assert.Equal(new[]{"grantId","id","kind"},op.Parameters!.Where(x=>x.Required).Select(x=>x.Name).Order());
        Assert.False(op.Parameters!.Single(x=>x.Name=="collectionId").Required);
        foreach(var name in new[]{"SoftwareSecretOutput","SoftwareSecretResource","SoftwareSecretProvisioning"})
        {
            var schema=doc.Components!.Schemas![name];Assert.Equal(schema.Properties!.Keys.Order(),schema.Required!.Order());
        }
    }
}
