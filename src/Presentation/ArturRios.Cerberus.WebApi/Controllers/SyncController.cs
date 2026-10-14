using System.Globalization;
using System.Net.Http.Headers;
using ArturRios.Cerberus.Command.Sync;
using ArturRios.Cerberus.Query.Sync;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/sync")]
public sealed class SyncController(QueryMediator queries, CommandMediator commands) : ControllerBase
{
    [HttpPost("edits")]
    [VaultProofBody]
    [SyncEditDiscriminators]
    [ProducesResponseType(typeof(DataOutput<SyncEditsOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<SyncEditsOutput?>>> Edits(
        [FromBody] UploadSyncEditsCommand command,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || !MediaTypeHeaderValue.TryParse(Request.ContentType, out var media) || media.Parameters.Count > 1
            || media.Parameters.Any(p => !string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase)))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor) || actor == Guid.Empty)
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var access) ? access.ToString() : null;
        command.SetContext(actor, vaultAccess);
        return (await commands.ExecuteCommandAsync<UploadSyncEditsCommand, SyncEditsOutput>(command, cancellationToken))
            .ToActionResult(statusMap: SyncEditsMessages.StatusCodes);
    }

    [HttpGet("changes")]
    [ProducesResponseType(typeof(DataOutput<SyncChangesOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(DataOutput<SyncChangesOutput>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<SyncChangesOutput?>>> Changes(
        [FromQuery] string? cursor, [FromQuery] int? pageSize,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Keys.Any(k => k is not ("cursor" or "pageSize"))
            || Request.Query.Any(q => q.Value.Count != 1) || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            || Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count > 0)
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (Request.Query.TryGetValue("pageSize", out var rawSize)
            && (!int.TryParse(rawSize, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || rawSize != size.ToString(CultureInfo.InvariantCulture)))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor) || actor == Guid.Empty)
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        cursor = Request.Query.TryGetValue("cursor", out var rawCursor) ? rawCursor.ToString() : null;
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var rawAccess) ? rawAccess.ToString() : null;
        return (await queries.ExecuteQueryAsync<DownloadSyncChangesQuery, SyncChangesOutput>(new(actor, vaultAccess, cursor, pageSize), cancellationToken))
            .ToActionResult(statusMap: SyncDownloadMessages.StatusCodes);
    }
}
