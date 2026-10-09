using ArturRios.Cerberus.Command.Folders;
using ArturRios.Cerberus.Query.Folders;
using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using ArturRios.Mediator.Query;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/folders")]
public sealed class FoldersController(CommandMediator commands, QueryMediator queries) : ControllerBase
{
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(DataOutput<FolderDetailsOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<FolderDetailsOutput?>>> Get([FromRoute] string id,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            || Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count != 0
            || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || !Guid.TryParseExact(id, "D", out var folderId) || folderId == Guid.Empty || id != folderId.ToString("D"))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var rawAccess) ? rawAccess.ToString() : null;
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        var result = await queries.ExecuteQueryAsync<GetFolderQuery, FolderDetailsOutput>(new(actor, vaultAccess, folderId), cancellationToken);
        return result.ToActionResult(statusMap: FolderReadMessages.StatusCodes);
    }

    [HttpGet]
    [ProducesResponseType(typeof(DataOutput<FolderListOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<FolderListOutput?>>> List(
        [FromQuery] string? pageSize, [FromQuery] string? cursor,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Keys.Any(k => k is not "pageSize" and not "cursor")
            || Request.Query.Any(p => p.Value.Count != 1)
            || HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            || Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count != 0
            || Request.Headers["X-Cerberus-Vault-Access"].Count > 1)
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        int? size = null;
        if (Request.Query.TryGetValue("pageSize", out var rawSize))
        {
            var text = rawSize.ToString();
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed <= 0 || text != parsed.ToString(CultureInfo.InvariantCulture))
                return BadRequest(ProcessOutput.New.WithError("validation_failed"));
            size = parsed;
        }
        cursor = Request.Query.TryGetValue("cursor", out var rawCursor) ? rawCursor.ToString() : null;
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var rawAccess) ? rawAccess.ToString() : null;
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        var result = await queries.ExecuteQueryAsync<ListFoldersQuery, FolderListOutput>(new(actor, vaultAccess, size, cursor), cancellationToken);
        return result.ToActionResult(statusMap: FolderListMessages.StatusCodes);
    }

    [HttpPost]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<CreateFolderOutput>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<CreateFolderOutput?>>> Create([FromBody] CreateFolderCommand command,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1)
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var raw) ? raw.ToString() : null;
        command.SetContext(actor, vaultAccess);
        var result = await commands.ExecuteCommandAsync<CreateFolderCommand, CreateFolderOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: FolderCreateMessages.StatusCodes);
    }
}
