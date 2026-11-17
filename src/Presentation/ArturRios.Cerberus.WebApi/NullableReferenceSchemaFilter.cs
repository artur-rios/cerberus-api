using System.Reflection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ArturRios.Cerberus.WebApi;

// Documentation only. The API writes null members as JSON null, and Swashbuckle drops
// the nullability of a member whose schema is a bare $ref. A member the C# annotations
// declare nullable (`Type?`) is published as OAS 3.1's nullable reference instead:
// anyOf with the $ref and the null type. The output envelope's own `data` is left out:
// its `T?` is generic, every success sets it, and failures are documented as
// FailureOutput (ErrorResponseFilter).
internal sealed class NullableReferenceSchemaFilter(ISerializerDataContractResolver contracts) : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema { Properties: not null } target
            || contracts.GetDataContractForType(context.Type) is not { DataType: DataType.Object, ObjectProperties: { } members })
            return;
        var nullability = new NullabilityInfoContext();
        foreach (var member in members)
        {
            if (target.Properties.TryGetValue(member.Name, out var property) && property is OpenApiSchemaReference reference
                && CanBeNull(member.MemberInfo, nullability))
                target.Properties[member.Name] = new OpenApiSchema
                { AnyOf = [reference, new OpenApiSchema { Type = JsonSchemaType.Null }] };
        }
    }

    internal static bool CanBeNull(MemberInfo? member, NullabilityInfoContext nullability)
    {
        if (member is not PropertyInfo and not FieldInfo || IsGenericPayload(member))
            return false;
        var type = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
        if (Nullable.GetUnderlyingType(type) is not null)
            return true;
        if (type.IsValueType)
            return false;
        var info = member is PropertyInfo property ? nullability.Create(property) : nullability.Create((FieldInfo)member);
        return info.ReadState == NullabilityState.Nullable;
    }

    private static bool IsGenericPayload(MemberInfo member) => member.DeclaringType is { IsGenericType: true } declaring
        && declaring.GetGenericTypeDefinition().GetMember(member.Name).SingleOrDefault() switch
        {
            PropertyInfo open => open.PropertyType.IsGenericParameter,
            FieldInfo open => open.FieldType.IsGenericParameter,
            _ => false
        };
}
