using ArturRios.Cerberus.Command.Identity;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/identity")]
public sealed class IdentityController(CommandMediator commands) : ControllerBase
{
    [HttpPut("me")]
    [ProducesResponseType(typeof(DataOutput<UpdateIdentityOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<UpdateIdentityOutput?>>> UpdateCurrent(
        [FromBody] UpdateIdentityCommand command,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1)
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var identityId))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var raw) ? raw.ToString() : null;
        // ProtectedEndpointMiddleware validates the original bearer and current identity.
        var token = Request.Headers.Authorization.ToString()[7..];
        command.SetActor(identityId, token, vaultAccess);
        var result = await commands.ExecuteCommandAsync<UpdateIdentityCommand, UpdateIdentityOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: UpdateIdentityMessages.StatusCodes);
    }
}
