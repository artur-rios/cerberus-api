using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
namespace ArturRios.Cerberus.WebApi.Tests;
public sealed class TrashEmptyContractTests
{
    [UnitFact]
    public void GivenEmptyTrashContract_WhenGeneratingOpenApi_ThenStrictRequiredBodyAndNonnullableCountsOnlyResult()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ApplicationName=typeof(Startup).Assembly.GetName().Name,Args=[]});builder.Services.AddCerberusContracts();using var host=builder.Build();var doc=host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        Assert.Contains("/api/trash",doc.Paths.Keys);Assert.Contains(HttpMethod.Delete,doc.Paths["/api/trash"].Operations!.Keys);var op=doc.Paths["/api/trash"].Operations![HttpMethod.Delete];
        Assert.Equal(new[]{"200","400","401","403","404","409","413","503"}.Order(),op.Responses!.Keys.Order());Assert.True(op.Responses["413"].Content is null or{Count:0});Assert.True(op.RequestBody!.Required);Assert.Equal("application/json",Assert.Single(op.RequestBody.Content!).Key);
        Assert.Equal("X-Cerberus-Vault-Access",Assert.Single(op.Parameters!).Name);
        foreach(var name in new[]{"EmptyTrashCommand","EmptyTrashOutput","TrashPurgedCount"}){var schema=doc.Components!.Schemas![name];Assert.Equal(schema.Properties!.Keys.Order(),schema.Required!.Order());Assert.All(schema.Properties.Values,p=>Assert.False(p.Type?.HasFlag(JsonSchemaType.Null)));}
        var command=doc.Components!.Schemas!["EmptyTrashCommand"];Assert.False(command.AdditionalPropertiesAllowed);Assert.Equal(new[]{"expectedRevision"},command.Properties!.Keys);
        Assert.Equal(new[]{"accountId","counts","purgedAt","totalPurged"},doc.Components.Schemas["EmptyTrashOutput"].Properties!.Keys.Order());var count=doc.Components.Schemas["TrashPurgedCount"];Assert.Equal(new[]{"count","kind"},count.Properties!.Keys.Order());Assert.Equal(new[]{"collection","folder","profile","record"},count.Properties["kind"].Enum!.Select(x=>x!.ToString()).Order());
    }
}
