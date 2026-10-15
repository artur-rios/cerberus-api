using System.Globalization;
using ArturRios.Cerberus.Command.Trash;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Mediator.Command;
using ArturRios.Cerberus.Query.Trash;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/trash")]
public sealed class TrashController(QueryMediator queries,CommandMediator commands):ControllerBase
{
    [HttpDelete]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<EmptyTrashOutput>),StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(void),StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<EmptyTrashOutput?>>> EmptyTrash([FromBody]EmptyTrashCommand command,
        [FromHeader(Name="X-Cerberus-Vault-Access")]string? vaultAccess,CancellationToken cancellationToken)
    {
        if(Request.Query.Count!=0 || Request.Headers["X-Cerberus-Vault-Access"].Count>1
            || Request.Headers.Keys.Any(k=>k.StartsWith("X-Cerberus-",StringComparison.OrdinalIgnoreCase)
                && !k.Equals("X-Cerberus-Vault-Access",StringComparison.OrdinalIgnoreCase)))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if(!Guid.TryParse(User.FindFirst("id")?.Value,out var actor))return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess=Request.Headers.TryGetValue("X-Cerberus-Vault-Access",out var raw)?raw.ToString():null;
        command.SetContext(actor,vaultAccess);
        var result=await commands.ExecuteCommandAsync<EmptyTrashCommand,EmptyTrashOutput>(command,cancellationToken);
        return result.ToActionResult(statusMap:EmptyTrashMessages.StatusCodes);
    }

    [HttpPost("{id}/restore")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<RestoreTrashOutput>),StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status410Gone)]
    [ProducesResponseType(typeof(void),StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<RestoreTrashOutput?>>> Restore([FromRoute]string id,[FromBody]RestoreTrashCommand command,
        [FromHeader(Name="X-Cerberus-Vault-Access")]string? vaultAccess,CancellationToken cancellationToken)
    {
        if(Request.Query.Count!=0 || Request.Headers["X-Cerberus-Vault-Access"].Count>1
            || Request.Headers.Keys.Any(k=>k.StartsWith("X-Cerberus-",StringComparison.OrdinalIgnoreCase)
                && !k.Equals("X-Cerberus-Vault-Access",StringComparison.OrdinalIgnoreCase))
            || !Guid.TryParseExact(id,"D",out var operationId) || operationId==Guid.Empty || id!=operationId.ToString("D"))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if(!Guid.TryParse(User.FindFirst("id")?.Value,out var actor))return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess=Request.Headers.TryGetValue("X-Cerberus-Vault-Access",out var raw)?raw.ToString():null;
        command.SetContext(actor,vaultAccess,operationId);
        var result=await commands.ExecuteCommandAsync<RestoreTrashCommand,RestoreTrashOutput>(command,cancellationToken);
        return result.ToActionResult(statusMap:RestoreTrashMessages.StatusCodes);
    }

    [HttpGet]
    [ProducesResponseType(typeof(DataOutput<TrashListOutput>),StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(void),StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<TrashListOutput?>>> List([FromQuery]string? pageSize,[FromQuery]string? cursor,
        [FromHeader(Name="X-Cerberus-Vault-Access")]string? vaultAccess,CancellationToken cancellationToken)
    {
        if(Request.Query.Keys.Any(k=>k is not "pageSize" and not "cursor") || Request.Query.Any(p=>p.Value.Count!=1)
            || HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody==true || Request.ContentLength>0
            || Request.Headers.TransferEncoding.Count!=0 || Request.Headers["X-Cerberus-Vault-Access"].Count>1
            || Request.Headers.Keys.Any(k=>k.StartsWith("X-Cerberus-",StringComparison.OrdinalIgnoreCase)
                && !k.Equals("X-Cerberus-Vault-Access",StringComparison.OrdinalIgnoreCase)))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        int? size=null;
        if(Request.Query.TryGetValue("pageSize",out var rawSize))
        {
            pageSize=rawSize.ToString();
            if(!int.TryParse(pageSize,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed)
                || parsed.ToString(CultureInfo.InvariantCulture)!=pageSize)return BadRequest(ProcessOutput.New.WithError("validation_failed"));
            size=parsed;
        }
        cursor=Request.Query.TryGetValue("cursor",out var rawCursor)?rawCursor.ToString():null;
        vaultAccess=Request.Headers.TryGetValue("X-Cerberus-Vault-Access",out var raw)?raw.ToString():null;
        if(!Guid.TryParse(User.FindFirst("id")?.Value,out var actor))return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        var result=await queries.ExecuteQueryAsync<ListTrashQuery,TrashListOutput>(new(actor,vaultAccess,size,cursor),cancellationToken);
        return result.ToActionResult(statusMap:TrashListMessages.StatusCodes);
    }
}
