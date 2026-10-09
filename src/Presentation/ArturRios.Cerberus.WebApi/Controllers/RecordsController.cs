using ArturRios.Cerberus.Command.Records;
using System.Globalization;
using ArturRios.Cerberus.Query.Records;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/records")]
public sealed class RecordsController(CommandMediator commands, QueryMediator queries) : ControllerBase
{
    [HttpPut("{id}/folder")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<MoveRecordOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<MoveRecordOutput?>>> Move([FromRoute] string id,
        [FromBody] MoveRecordCommand command, [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess,
        CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || !Guid.TryParseExact(id, "D", out var recordId) || recordId == Guid.Empty || id != recordId.ToString("D"))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var raw) ? raw.ToString() : null;
        command.SetContext(actor, vaultAccess, recordId);
        var result = await commands.ExecuteCommandAsync<MoveRecordCommand, MoveRecordOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: MoveRecordMessages.StatusCodes);
    }

    [HttpDelete("{id}")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<DeleteRecordOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<DeleteRecordOutput?>>> Delete([FromRoute] string id,
        [FromBody] DeleteRecordCommand command, [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess,
        CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || !Guid.TryParseExact(id, "D", out var recordId) || recordId == Guid.Empty || id != recordId.ToString("D"))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var raw) ? raw.ToString() : null;
        command.SetContext(actor, vaultAccess, recordId);
        var result = await commands.ExecuteCommandAsync<DeleteRecordCommand, DeleteRecordOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: DeleteRecordMessages.StatusCodes);
    }

    [HttpPut("{id}")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<UpdateRecordOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<UpdateRecordOutput?>>> Update([FromRoute] string id,
        [FromBody] UpdateRecordCommand command, [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess,
        CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || !Guid.TryParseExact(id, "D", out var recordId) || recordId == Guid.Empty || id != recordId.ToString("D"))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var raw) ? raw.ToString() : null;
        command.SetContext(actor, vaultAccess, recordId);
        var result = await commands.ExecuteCommandAsync<UpdateRecordCommand, UpdateRecordOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: RecordUpdateMessages.StatusCodes);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(DataOutput<RecordDetailsOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<RecordDetailsOutput?>>> Get([FromRoute] string id,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            || Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count != 0
            || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || !Guid.TryParseExact(id, "D", out var recordId) || recordId == Guid.Empty || id != recordId.ToString("D"))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var rawAccess) ? rawAccess.ToString() : null;
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        var result = await queries.ExecuteQueryAsync<GetRecordQuery, RecordDetailsOutput>(new(actor, vaultAccess, recordId), cancellationToken);
        return result.ToActionResult(statusMap: RecordReadMessages.StatusCodes);
    }

    [HttpGet]
    [ProducesResponseType(typeof(DataOutput<RecordListOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<RecordListOutput?>>> List(
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
        var result = await queries.ExecuteQueryAsync<ListRecordsQuery, RecordListOutput>(new(actor, vaultAccess, size, cursor), cancellationToken);
        return result.ToActionResult(statusMap: RecordListMessages.StatusCodes);
    }

    [HttpPost]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<CreateRecordOutput>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<CreateRecordOutput?>>> Create([FromBody] CreateRecordCommand command,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1)
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var raw) ? raw.ToString() : null;
        command.SetContext(actor, vaultAccess);
        var result = await commands.ExecuteCommandAsync<CreateRecordCommand, CreateRecordOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: RecordCreateMessages.StatusCodes);
    }
}
