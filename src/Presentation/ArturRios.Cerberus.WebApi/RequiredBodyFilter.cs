using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ArturRios.Cerberus.WebApi;

// Documentation only. Every [FromBody] command is a non-nullable parameter and MVC
// keeps AllowEmptyInputInBodyModelBinding off, so a missing body is already answered
// with 400 validation_failed; the document says the body is required.
internal sealed class RequiredBodyFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.RequestBody is OpenApiRequestBody body
            && context.ApiDescription.ParameterDescriptions.Any(p => p.Source == BindingSource.Body))
            body.Required = true;
    }
}
