using System.Text.Json;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Output;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ArturRios.Cerberus.WebApi;

// Validate required metadata before model binding can attempt to instantiate an
// abstract edit record. Native strict polymorphic deserialization remains intact.
[AttributeUsage(AttributeTargets.Method)]
public sealed class SyncEditDiscriminatorsAttribute : Attribute, IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (context.HttpContext.Items[VaultProofBodyMiddleware.BodyKey] is not byte[] raw || !Valid(raw))
        {
            context.Result = new BadRequestObjectResult(ProcessOutput.New.WithError("validation_failed"));
            return;
        }
        await next();
    }
    private static bool Valid(byte[] raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("edits", out var edits) || edits.ValueKind != JsonValueKind.Array) return false;
            foreach (var edit in edits.EnumerateArray())
            {
                if (edit.ValueKind != JsonValueKind.Object || edit.EnumerateObject().Count(p => p.Name == "editType") != 1
                    || !edit.TryGetProperty("editType", out var type) || type.ValueKind != JsonValueKind.String
                    || type.GetString() is not ("record-create" or "record-update" or "folder-create" or "folder-update"
                        or "collection-create" or "collection-update" or "profile-create" or "profile-update")) return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }
}
