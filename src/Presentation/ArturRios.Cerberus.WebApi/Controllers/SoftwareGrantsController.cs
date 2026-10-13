using ArturRios.Cerberus.Command.Software;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/software-grants")]
public sealed class SoftwareGrantsController(CommandMediator commands) : ControllerBase
{
    [HttpDelete("{id}")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<RevokeSoftwareAccessOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<RevokeSoftwareAccessOutput?>>> RevokeAccess(
        [FromRoute] string id, [FromBody] RevokeSoftwareAccessCommand command,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || !MediaTypeHeaderValue.TryParse(Request.ContentType, out var media)
            || media.Parameters.Count > 1
            || media.Parameters.Any(p => !string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase))
            || !Guid.TryParseExact(id, "D", out var grantId) || grantId == Guid.Empty || id != grantId.ToString("D"))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor) || actor == Guid.Empty)
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var raw) ? raw.ToString() : null;
        command.SetContext(actor, vaultAccess, grantId);
        var result = await commands.ExecuteCommandAsync<RevokeSoftwareAccessCommand, RevokeSoftwareAccessOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: SoftwareGrantRevocationMessages.StatusCodes);
    }

    [HttpPost]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<GrantSoftwareAccessOutput>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<GrantSoftwareAccessOutput?>>> GrantAccess(
        [FromBody] GrantSoftwareAccessCommand command,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || !MediaTypeHeaderValue.TryParse(Request.ContentType, out var media)
            || media.Parameters.Count > 1
            || media.Parameters.Any(p => !string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase)))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor) || actor == Guid.Empty)
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var raw) ? raw.ToString() : null;
        // Current-human revalidation forwards the original bearer in ProtectedEndpointMiddleware.
        // The application verifier uses its independently configured scoped service credential.
        command.SetContext(actor, vaultAccess);
        var result = await commands.ExecuteCommandAsync<GrantSoftwareAccessCommand, GrantSoftwareAccessOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: SoftwareGrantMessages.StatusCodes);
    }
}
