using System.Text.Json;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.Query.Trash;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
namespace ArturRios.Cerberus.WebApi;

internal sealed class TrashListSchemaFilter:ISchemaFilter
{
    public void Apply(IOpenApiSchema schema,SchemaFilterContext context)
    {
        if(schema is not OpenApiSchema {Properties:not null} target)return;
        if(context.Type==typeof(TrashListOutput))
        {target.Required=target.Properties.Keys.ToHashSet(StringComparer.Ordinal);return;}
        if(!typeof(TrashListItem).IsAssignableFrom(context.Type) || context.Type.IsAbstract)return;
        target.AllOf?.Clear();
        foreach(var p in typeof(TrashListItem).GetProperties())
            target.Properties[JsonNamingPolicy.CamelCase.ConvertName(p.Name)]=context.SchemaGenerator.GenerateSchema(p.PropertyType,context.SchemaRepository);
        var kind=context.Type==typeof(TrashRecordItem)?"record":context.Type==typeof(TrashFolderItem)?"folder"
            :context.Type==typeof(TrashCollectionItem)?"collection":"profile";
        target.Properties["kind"]=new OpenApiSchema{Type=JsonSchemaType.String,Enum=[JsonValue.Create(kind)]};
        target.Required=target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
    }
}
