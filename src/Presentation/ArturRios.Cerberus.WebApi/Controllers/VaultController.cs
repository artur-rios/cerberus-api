using ArturRios.Cerberus.Command.Protection;
using ArturRios.Cerberus.Query.Protection;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/vault")]
[ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
[ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
public sealed class VaultController(CommandMediator commands, QueryMediator queries, TimeProvider clock) : ControllerBase
{
    [HttpPost("protection")]
    [ProducesResponseType(typeof(DataOutput<VaultInitializationOutput>), StatusCodes.Status201Created)]
    public async Task<ActionResult<DataOutput<VaultInitializationOutput?>>> Initialize([FromBody] InitializeVaultCommand command, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0) return Invalid();
        command.SetActor(Actor());
        var result = await commands.ExecuteCommandAsync<InitializeVaultCommand, VaultInitializationOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: VaultProtectionMessages.StatusCodes);
    }

    [HttpGet("protection")]
    [ProducesResponseType(typeof(DataOutput<VaultProtectionOutput>), StatusCodes.Status200OK)]
    public async Task<ActionResult<DataOutput<VaultProtectionOutput?>>> Read(CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            || Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count != 0) return Invalid();
        var result = await queries.ExecuteQueryAsync<GetVaultProtectionQuery, VaultProtectionOutput>(new GetVaultProtectionQuery(Actor()), cancellationToken);
        return result.ToActionResult(statusMap: VaultProtectionQueryMessages.StatusCodes);
    }

    [HttpPost("challenges")]
    [ProducesResponseType(typeof(DataOutput<VaultChallengeOutput>), StatusCodes.Status201Created)]
    public async Task<ActionResult<DataOutput<VaultChallengeOutput?>>> Challenge([FromBody] IssueVaultChallengeCommand command, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0) return Invalid();
        if (!FreshIdentity.IsFresh(User, clock.GetUtcNow())) return Unauthorized(ProcessOutput.New.WithError("fresh_authentication_required"));
        command.SetActor(Actor());
        var result = await commands.ExecuteCommandAsync<IssueVaultChallengeCommand, VaultChallengeOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: VaultProtectionMessages.StatusCodes);
    }

    [HttpPost("unlock")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<VaultUnlockOutput>), StatusCodes.Status200OK)]
    public async Task<ActionResult<DataOutput<VaultUnlockOutput?>>> Unlock([FromBody] UnlockVaultCommand command,
        [FromHeader(Name = "X-Cerberus-Challenge-Id")] string? challengeId,
        [FromHeader(Name = "X-Cerberus-Proof")] string? proof, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0) return Invalid();
        var ids = Request.Headers["X-Cerberus-Challenge-Id"]; var proofs = Request.Headers["X-Cerberus-Proof"];
        if (ids.Count == 0 || proofs.Count == 0) return Unauthorized(ProcessOutput.New.WithError("vault_proof_rejected"));
        challengeId = ids.ToString(); proof = proofs.ToString();
        if (ids.Count != 1 || proofs.Count != 1 || !Guid.TryParseExact(challengeId, "D", out var id) || id == Guid.Empty
            || challengeId != id.ToString("D") || HttpContext.Items[VaultProofBodyMiddleware.BodyKey] is not byte[] raw) return Invalid();
        command.SetProof(Actor(), id, proof, raw);
        var result = await commands.ExecuteCommandAsync<UnlockVaultCommand, VaultUnlockOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: VaultProtectionMessages.StatusCodes);
    }
    private Guid Actor() => Guid.TryParse(User.FindFirst("id")?.Value, out var actor) ? actor : Guid.Empty;
    private BadRequestObjectResult Invalid() => BadRequest(ProcessOutput.New.WithError("validation_failed"));
}
