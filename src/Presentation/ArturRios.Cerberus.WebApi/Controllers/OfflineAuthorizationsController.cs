using System.Net.Http.Headers;
using ArturRios.Cerberus.Command.Offline;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/offline-authorizations")]
public sealed class OfflineAuthorizationsController(CommandMediator commands) : ControllerBase
{
    [HttpPost]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<OfflineAuthorizationOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<OfflineAuthorizationOutput?>>> Renew(
        [FromBody] RenewOfflineAuthorizationCommand command,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || !MediaTypeHeaderValue.TryParse(Request.ContentType, out var media) || media.Parameters.Count > 1
            || media.Parameters.Any(p => !string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase)))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var identity) || identity == Guid.Empty)
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var access) ? access.ToString() : null;
        command.SetContext(identity, vaultAccess);
        return (await commands.ExecuteCommandAsync<RenewOfflineAuthorizationCommand, OfflineAuthorizationOutput>(command, cancellationToken))
            .ToActionResult(statusMap: OfflineAuthorizationMessages.StatusCodes);
    }
}
