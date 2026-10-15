using ArturRios.Cerberus.Command.Trash;
using ArturRios.Cerberus.Domain.Trash;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
namespace ArturRios.Cerberus.WebApi;

internal sealed class TrashRestoreSchemaFilter:ISchemaFilter
{
    public void Apply(IOpenApiSchema schema,SchemaFilterContext context)
    {
        if(schema is not OpenApiSchema {Properties:not null} target)return;
        if(context.Type!=typeof(RestoreTrashCommand)&&context.Type!=typeof(RestoreTrashOutput)
            &&context.Type!=typeof(TrashRestoredItem)&&context.Type!=typeof(TrashRestoreOmission))return;
        target.Required=target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
        if(context.Type==typeof(RestoreTrashCommand))target.AdditionalPropertiesAllowed=false;
    }
}
