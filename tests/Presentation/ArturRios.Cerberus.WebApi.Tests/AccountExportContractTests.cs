using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Swashbuckle.AspNetCore.Swagger;

namespace ArturRios.Cerberus.WebApi.Tests;

public sealed class AccountExportContractTests
{
    [UnitTheory][InlineData(1L,true)][InlineData(0L,false)][InlineData(-1L,false)][InlineData(9007199254740992L,false)]
    public void GivenMvcExportInput_WhenValidatingBoundRecord_ThenAcceptOnlySafeRevisionWithoutFrameworkFailure(long revision,bool valid)
    {
        // Exercise the actual MVC record validator outside the safe HTTP exception boundary.
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ApplicationName=typeof(Startup).Assembly.GetName().Name,Args=[]});builder.Services.AddCerberusContracts();using var host=builder.Build();
        var context=new ActionContext(new DefaultHttpContext{RequestServices=host.Services},new RouteData(),new ActionDescriptor(),new ModelStateDictionary());host.Services.GetRequiredService<IObjectModelValidator>().Validate(context,null,"",new AccountExportInput(revision));Assert.Equal(valid,context.ModelState.IsValid);
    }
    [UnitFact]
    public void GivenExportContract_WhenGeneratingOpenApi_ThenDescribeActualClosedArchiveAndProcessErrors()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ApplicationName=typeof(Startup).Assembly.GetName().Name,Args=[]});builder.Services.AddCerberusContracts();using var host=builder.Build();var doc=host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        Assert.Contains("/api/accounts/me/export",doc.Paths.Keys);var op=doc.Paths["/api/accounts/me/export"].Operations![HttpMethod.Post];
        Assert.Equal(new[]{"200","400","401","403","404","409","413","503"}.Order(),op.Responses!.Keys.Order());Assert.True(op.RequestBody!.Required);Assert.Equal(new[]{"application/json"},op.RequestBody.Content!.Keys);
        var access=Assert.Single(op.Parameters!);Assert.Equal("X-Cerberus-Vault-Access",access.Name);Assert.True(access.Required);Assert.True(op.Responses["413"].Content is null or {Count:0});
        var schemas=doc.Components!.Schemas!;var archive=schemas["AccountExportArchive"];Assert.Equal(new[]{"complete","entries","entryCount","format","header"},archive.Required!.Order());Assert.Equal("cerberus-account-export-v1",Assert.Single(archive.Properties!["format"].Enum!).ToString());Assert.Equal("true",Assert.Single(archive.Properties["complete"].Enum!).ToString());Assert.Equal(12,archive.Properties["entries"].Items!.OneOf!.Count);
        var variants=new Dictionary<string,string>{["AccountExportAccount"]="account",["AccountExportProtection"]="protection",["AccountExportProfile"]="profile",["AccountExportRecord"]="record",["AccountExportFolder"]="folder",["AccountExportCollection"]="collection",["AccountExportRelationship"]="relationship",["AccountExportCollectionGrant"]="collection-grant",["AccountExportSharedReference"]="shared-collection-reference",["AccountExportSoftwareGrant"]="software-grant",["AccountExportSoftwareTarget"]="software-target",["AccountExportTrashOperation"]="trash-operation"};
        foreach(var (name,kind) in variants){var schema=schemas[name];Assert.Equal(schema.Properties!.Keys.Order(),schema.Required!.Order());Assert.Contains("entryType",schema.Required!);Assert.Equal(kind,Assert.Single(schema.Properties["entryType"].Enum!).ToString());Assert.False(schema.AdditionalPropertiesAllowed);}
        var header=schemas["AccountExportHeader"];Assert.Equal(header.Properties!.Keys.Order(),header.Required!.Order());Assert.Contains("renewalIntervalSeconds",header.Required!);Assert.Equal(new[]{"expectedRevision"},schemas["AccountExportInput"].Properties!.Keys);
        foreach(var status in new[]{"400","401","403","404","409","503"})Assert.Equal("ProcessOutput",Assert.IsType<OpenApiSchemaReference>(op.Responses[status].Content!["application/json"].Schema!).Reference.Id);
    }
}
