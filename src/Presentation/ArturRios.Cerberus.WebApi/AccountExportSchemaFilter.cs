using System.Globalization;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.WebApi.Streaming;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ArturRios.Cerberus.WebApi;

internal sealed class AccountExportSchemaFilter:ISchemaFilter
{
    internal static readonly IReadOnlyDictionary<Type,string> Kinds=new Dictionary<Type,string>
    {
        [typeof(AccountExportAccount)]="account",[typeof(AccountExportProtection)]="protection",[typeof(AccountExportProfile)]="profile",
        [typeof(AccountExportRecord)]="record",[typeof(AccountExportFolder)]="folder",[typeof(AccountExportCollection)]="collection",
        [typeof(AccountExportRelationship)]="relationship",[typeof(AccountExportCollectionGrant)]="collection-grant",[typeof(AccountExportSharedReference)]="shared-collection-reference",
        [typeof(AccountExportSoftwareGrant)]="software-grant",[typeof(AccountExportSoftwareTarget)]="software-target",[typeof(AccountExportTrashOperation)]="trash-operation"
    };
    public void Apply(IOpenApiSchema schema,SchemaFilterContext context)
    {
        if(schema is not OpenApiSchema {Properties:not null} target)return;
        var entry=Kinds.TryGetValue(context.Type,out var kind);
        if(!entry&&context.Type!=typeof(AccountExportArchive)&&context.Type!=typeof(AccountExportHeader)&&context.Type!=typeof(AccountExportInput))return;
        target.AdditionalPropertiesAllowed=false;
        if(entry){target.AllOf?.Clear();target.Properties["entryType"]=Text(kind!);}
        if(context.Type==typeof(AccountExportArchive))
        {
            target.Properties["format"]=Text(AccountExportStreamResult.Format);
            target.Properties["complete"]=new OpenApiSchema{Type=JsonSchemaType.Boolean,Enum=[JsonValue.Create(true)]};
        }
        foreach(var name in new[]{"expectedRevision","revision","serverSequence","policyRevision","revocationGeneration","keyEpoch","recoveryGeneration","grantRevision","renewalIntervalSeconds","entryCount"})
            if(target.Properties.TryGetValue(name,out var property)&&property is OpenApiSchema number)
            {number.Minimum=name=="entryCount"?"0":"1";number.Maximum=ProtocolBinary.MaxInteger.ToString(CultureInfo.InvariantCulture);}
        if(target.Properties.ContainsKey("access"))target.Properties["access"]=Text("read-only","read-write");
        if(target.Properties.ContainsKey("state"))target.Properties["state"]=Text("active","revoked");
        if(context.Type==typeof(AccountExportSoftwareTarget))target.Properties["resourceKind"]=Text("record","collection");
        if(context.Type==typeof(AccountExportTrashOperation))target.Properties["rootKind"]=Text("profile","record","folder","collection");
        if(context.Type==typeof(AccountExportRelationship))
        {target.Properties["relationshipKind"]=Text("current","historical");target.Properties["sourceKind"]=Text("profile","collection","record","folder");target.Properties["targetKind"]=Text("record","folder","collection");}
        target.Required=target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
    }
    private static OpenApiSchema Text(params string[] values)=>new(){Type=JsonSchemaType.String,Enum=values.Select(v=>(JsonNode)JsonValue.Create(v)!).ToList()};
}
