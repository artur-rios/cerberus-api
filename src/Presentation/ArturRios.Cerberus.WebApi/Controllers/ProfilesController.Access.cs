using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Mvc;
namespace ArturRios.Cerberus.WebApi.Controllers;

public sealed partial class ProfilesController
{
    [HttpPost("{id}/access/challenges")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<IssueProfileAccessChallengeOutput>),StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<DataOutput<IssueProfileAccessChallengeOutput?>>> AccessChallenge([FromRoute]string id,
        [FromBody]IssueProfileAccessChallengeCommand command,CancellationToken cancellationToken)
    {
        if(!AccessProfileRoute(id,out var profileId))return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if(!FreshIdentity.IsFresh(User,clock.GetUtcNow()))return Unauthorized(ProcessOutput.New.WithError("fresh_authentication_required"));
        command.SetContext(AccessActor(),profileId);
        var result=await commands.ExecuteCommandAsync<IssueProfileAccessChallengeCommand,IssueProfileAccessChallengeOutput>(command,cancellationToken);
        return result.ToActionResult(statusMap:ProfileAccessMessages.StatusCodes);
    }

    [HttpPost("{id}/access")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<OpenProfileAccessOutput>),StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<DataOutput<OpenProfileAccessOutput?>>> OpenAccess([FromRoute]string id,[FromBody]OpenProfileAccessCommand command,
        [FromHeader(Name="X-Cerberus-Challenge-Id")]string? challengeId,
        [FromHeader(Name="X-Cerberus-Proof")]string? proof,CancellationToken cancellationToken)
    {
        if(!AccessProfileRoute(id,out var profileId))return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        var ids=Request.Headers["X-Cerberus-Challenge-Id"];var proofs=Request.Headers["X-Cerberus-Proof"];
        if(ids.Count==0 || proofs.Count==0)return Unauthorized(ProcessOutput.New.WithError("vault_proof_rejected"));
        challengeId=ids.ToString();proof=proofs.ToString();
        if(ids.Count!=1 || proofs.Count!=1 || !Guid.TryParseExact(challengeId,"D",out var challenge) || challenge==Guid.Empty
            || challengeId!=challenge.ToString("D") || HttpContext.Items[VaultProofBodyMiddleware.BodyKey] is not byte[] raw)
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        command.SetContext(AccessActor(),profileId,challenge,proof,raw);
        var result=await commands.ExecuteCommandAsync<OpenProfileAccessCommand,OpenProfileAccessOutput>(command,cancellationToken);
        return result.ToActionResult(statusMap:ProfileAccessMessages.StatusCodes);
    }
    private Guid AccessActor()=>Guid.TryParse(User.FindFirst("id")?.Value,out var actor)?actor:Guid.Empty;
    private bool AccessProfileRoute(string id,out Guid profileId)=>Guid.TryParseExact(id,"D",out profileId) && profileId!=Guid.Empty
        && id==profileId.ToString("D") && Request.Query.Count==0;
}
