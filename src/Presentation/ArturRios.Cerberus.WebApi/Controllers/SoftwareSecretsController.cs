using System.Globalization;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.Query.Software;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/software-secrets")]
public sealed class SoftwareSecretsController(QueryMediator queries):ControllerBase
{
    [HttpGet]
    [ApplicationAuthentication]
    [ProducesResponseType(typeof(DataOutput<SoftwareSecretOutput>),StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(void),StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<SoftwareSecretOutput?>>> Get(
        [FromQuery,BindRequired]string? grantId,[FromQuery,BindRequired]string? kind,[FromQuery,BindRequired]string? id,[FromQuery]string? collectionId,CancellationToken cancellationToken)
    {
        if(Request.Query.Keys.Any(x=>x is not "grantId" and not "kind" and not "id" and not "collectionId")
            ||Request.Query.Any(x=>x.Value.Count!=1)||Request.Query.Count is <3 or >4
            ||HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody==true||Request.ContentLength>0||Request.Headers.TransferEncoding.Count!=0
            ||Request.Headers.Keys.Any(x=>x.StartsWith("X-Cerberus-",StringComparison.OrdinalIgnoreCase))
            ||!Canonical(grantId,out var grant)||!Canonical(id,out var target))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        Guid? collection=null;
        if(Request.Query.ContainsKey("collectionId"))
        {
            if(!Canonical(collectionId,out var parsed))return BadRequest(ProcessOutput.New.WithError("validation_failed"));
            collection=parsed;
        }
        var input=new SoftwareSecretInput(grant,kind??"",target,collection);
        if(!input.IsValid())return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        // The middleware authenticated these claims. The live check uses this same original bearer.
        if(!Guid.TryParse(User.FindFirst("id")?.Value,out var application)||!Guid.TryParse(User.FindFirst("scopeId")?.Value,out var scope)
            ||!Guid.TryParse(User.FindFirst("credentialId")?.Value,out var credential)
            ||!long.TryParse(User.FindFirst("exp")?.Value,NumberStyles.None,CultureInfo.InvariantCulture,out var expiry)
            ||expiry is <=0||expiry>DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        var identity=new SoftwareIdentity(application,scope,credential,DateTimeOffset.FromUnixTimeSeconds(expiry));
        var bearer=Request.Headers.Authorization.ToString()[7..];
        var result=await queries.ExecuteQueryAsync<GetSoftwareSecretQuery,SoftwareSecretOutput>(new(identity,bearer,input),cancellationToken);
        return result.ToActionResult(statusMap:SoftwareSecretMessages.StatusCodes);
    }
    private static bool Canonical(string? text,out Guid value)=>Guid.TryParseExact(text,"D",out value)&&value!=Guid.Empty&&text==value.ToString("D");
}
