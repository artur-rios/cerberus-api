using System.Globalization;
using System.Net.Http.Headers;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Query.Accounts;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Cerberus.WebApi.Streaming;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/accounts/me/export")]
[Produces("application/json")]
public sealed class AccountExportController(QueryMediator queries):ControllerBase
{
    [HttpPost]
    [VaultProofBody]
    [ProducesResponseType(typeof(AccountExportArchive),StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Export([FromBody]AccountExportInput input,
        [FromHeader(Name="X-Cerberus-Vault-Access")]string? access,CancellationToken cancellationToken)
    {
        if(Request.Query.Count!=0||Request.Headers["X-Cerberus-Vault-Access"].Count>1
            ||!MediaTypeHeaderValue.TryParse(Request.ContentType,out var media)||media.Parameters.Count>1
            ||media.Parameters.Any(p=>!string.Equals(p.Name,"charset",StringComparison.OrdinalIgnoreCase)))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if(!Guid.TryParse(User.FindFirst("id")?.Value,out var actor)||actor==Guid.Empty)
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        var issued=User.FindAll("iat").ToArray();
        if(issued.Length!=1||!long.TryParse(issued[0].Value,NumberStyles.None,CultureInfo.InvariantCulture,out var issuedAt))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        access=Request.Headers.TryGetValue("X-Cerberus-Vault-Access",out var raw)?raw.ToString():null;
        var result=await queries.ExecuteQueryAsync<ExportAccountQuery,AccountExportOutput>(new(actor,issuedAt,access,input.ExpectedRevision),cancellationToken);
        var lease=result.Data?.Lease;
        if(result.Success&&lease is not null)return new AccountExportStreamResult(lease);
        if(lease is not null){try{await lease.DisposeAsync();}catch(Exception){/* Unpublished ownership never escapes. */}}
        var code=result.Errors.FirstOrDefault(e=>AccountExportMessages.StatusCodes.ContainsKey(e))??"persistence_unavailable";
        return StatusCode(AccountExportMessages.StatusCodes[code],ProcessOutput.New.WithError(code));
    }
}
