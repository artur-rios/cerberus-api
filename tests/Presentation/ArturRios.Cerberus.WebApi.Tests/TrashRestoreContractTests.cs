using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
namespace ArturRios.Cerberus.WebApi.Tests;
public class TrashRestoreContractTests
{
    [UnitFact]
    public void GivenRestoreContract_WhenGeneratingOpenApi_ThenRequiredStrictBodyAndMetadataAndEmpty413()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ApplicationName=typeof(Startup).Assembly.GetName().Name,Args=[]});builder.Services.AddCerberusContracts();using var host=builder.Build();var doc=host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        Assert.Contains("/api/trash/{id}/restore",doc.Paths.Keys);var op=doc.Paths["/api/trash/{id}/restore"].Operations![HttpMethod.Post];
        Assert.Equal(new[]{"200","400","401","403","404","409","410","413","503"}.Order(),op.Responses!.Keys.Order());Assert.True(op.Responses["413"].Content is null or{Count:0});
        Assert.True(op.RequestBody!.Required);Assert.Equal("application/json",Assert.Single(op.RequestBody.Content!).Key);
        Assert.Equal("uuid",op.Parameters!.Single(x=>x.Name=="id").Schema!.Format);
        foreach(var name in new[]{"RestoreTrashCommand","RestoreTrashOutput","TrashRestoredItem","TrashRestoreOmission"}){var schema=doc.Components!.Schemas![name];Assert.Equal(schema.Properties!.Keys.Order(),schema.Required!.Order());}
        Assert.Equal(new[]{"expectedRevision"},doc.Components!.Schemas!["RestoreTrashCommand"].Properties!.Keys);
        Assert.Equal(new[]{"items","omissions","operationId","restoredAt","rootKind","rootResourceId"},doc.Components.Schemas["RestoreTrashOutput"].Properties!.Keys.Order());
    }
}
