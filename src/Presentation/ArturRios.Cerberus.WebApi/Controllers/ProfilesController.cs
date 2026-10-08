using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Mvc;
namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/profiles")]
public sealed class ProfilesController(CommandMediator commands):ControllerBase
{
    [HttpPost]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<CreateProfileOutput>),StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<CreateProfileOutput?>>> Create([FromBody]CreateProfileCommand command,
        [FromHeader(Name="X-Cerberus-Vault-Access")]string? vaultAccess,CancellationToken cancellationToken)
    {
        if(Request.Query.Count!=0 || Request.Headers["X-Cerberus-Vault-Access"].Count>1)return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if(!Guid.TryParse(User.FindFirst("id")?.Value,out var actor))return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess=Request.Headers.TryGetValue("X-Cerberus-Vault-Access",out var raw)?raw.ToString():null;
        command.SetContext(actor,vaultAccess);
        var result=await commands.ExecuteCommandAsync<CreateProfileCommand,CreateProfileOutput>(command,cancellationToken);
        return result.ToActionResult(statusMap:ProfileMessages.StatusCodes);
    }
}
