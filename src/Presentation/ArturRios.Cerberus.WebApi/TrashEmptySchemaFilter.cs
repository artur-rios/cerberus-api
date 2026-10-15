using System.Text.Json.Nodes;
using ArturRios.Cerberus.Command.Trash;
using ArturRios.Cerberus.Domain.Trash;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
namespace ArturRios.Cerberus.WebApi;

internal sealed class TrashEmptySchemaFilter:ISchemaFilter
{
    public void Apply(IOpenApiSchema schema,SchemaFilterContext context)
    {
        if(schema is not OpenApiSchema {Properties:not null} target)return;
        if(context.Type!=typeof(EmptyTrashCommand)&&context.Type!=typeof(EmptyTrashOutput)&&context.Type!=typeof(TrashPurgedCount))return;
        target.Required=target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
        foreach(var property in target.Properties.Values.OfType<OpenApiSchema>())
            if(property.Type is {} type)property.Type=type & ~JsonSchemaType.Null;
        if(context.Type==typeof(EmptyTrashCommand))target.AdditionalPropertiesAllowed=false;
        if(context.Type==typeof(EmptyTrashOutput)&&target.Properties["counts"] is OpenApiSchema counts)
        {counts.Type=JsonSchemaType.Array;counts.MinItems=4;counts.MaxItems=4;}
        if(context.Type==typeof(TrashPurgedCount))target.Properties["kind"]=new OpenApiSchema{
            Type=JsonSchemaType.String,Enum=[JsonValue.Create("record"),JsonValue.Create("folder"),JsonValue.Create("collection"),JsonValue.Create("profile")]};
    }
}
