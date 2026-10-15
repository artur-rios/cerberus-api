using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;
namespace ArturRios.Cerberus.WebApi.Tests;
public sealed class TrashListContractTests
{
    [UnitFact]
    public void GivenTrashListContract_WhenGeneratingOpenApi_ThenExactFourNativeKindsAndRequiredOutput()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ApplicationName=typeof(Startup).Assembly.GetName().Name,Args=[]});
        builder.Services.AddCerberusContracts();using var host=builder.Build();var doc=host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        Assert.Contains("/api/trash",doc.Paths.Keys);var op=Assert.Single(doc.Paths["/api/trash"].Operations!).Value;
        Assert.Null(op.RequestBody);Assert.Equal(new[]{"400","401","403","404","413","503","200"}.Order(),op.Responses!.Keys.Order());
        Assert.True(op.Responses["413"].Content is null or {Count:0});
        Assert.Equal(new[]{"X-Cerberus-Vault-Access","cursor","pageSize"}.Order(),op.Parameters!.Select(x=>x.Name).Order());
        foreach(var kind in new[]{"Record","Folder","Collection","Profile"}){
            var schema=doc.Components!.Schemas!["Trash"+kind+"Item"];Assert.Equal(schema.Properties!.Keys.Order(),schema.Required!.Order());
            Assert.Contains("operationId",schema.Required!);Assert.Contains("deletedAt",schema.Required!);Assert.Contains("purgeAt",schema.Required!);Assert.Contains("kind",schema.Required!);
            Assert.Equal(kind.ToLowerInvariant(),Assert.Single(schema.Properties["kind"].Enum!).ToString());}
        var output=doc.Components!.Schemas!["TrashListOutput"];Assert.Equal(new[]{"items","nextCursor"},output.Required!.Order());
        var item=output.Properties!["items"].Items!;Assert.Equal(4,item.OneOf!.Count);
    }
}
