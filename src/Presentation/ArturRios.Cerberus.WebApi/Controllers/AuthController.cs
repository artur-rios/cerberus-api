using ArturRios.Cerberus.Command.Authentication;
using ArturRios.Cerberus.WebApi.Middleware;
using System.Net.Http.Headers;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/auth")]
[ProducesResponseType(typeof(DataOutput<AuthenticationOutput>), StatusCodes.Status200OK)]
[ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
public sealed class AuthController(CommandMediator commands) : ControllerBase
{
    [HttpPost("closure/login")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<ClosureRecoveryAuthenticationOutput>),StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<DataOutput<ClosureRecoveryAuthenticationOutput?>>> ClosureLogin([FromBody]ClosureRecoveryLoginCommand command,CancellationToken cancellationToken)
    {
        if(!StrictRecoveryRequest())return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        return(await commands.ExecuteCommandAsync<ClosureRecoveryLoginCommand,ClosureRecoveryAuthenticationOutput>(command,cancellationToken))
            .ToActionResult(statusMap:ClosureRecoveryAuthenticationMessages.StatusCodes);
    }
    [HttpPost("closure/2fa/verify")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<ClosureRecoveryAuthenticationOutput>),StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<DataOutput<ClosureRecoveryAuthenticationOutput?>>> ClosureVerifyChallenge([FromBody]ClosureRecoveryVerifyChallengeCommand command,CancellationToken cancellationToken)
    {
        if(!StrictRecoveryRequest())return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        return(await commands.ExecuteCommandAsync<ClosureRecoveryVerifyChallengeCommand,ClosureRecoveryAuthenticationOutput>(command,cancellationToken))
            .ToActionResult(statusMap:ClosureRecoveryAuthenticationMessages.StatusCodes);
    }
    private bool StrictRecoveryRequest()=>Request.Query.Count==0&&Request.Headers["X-Cerberus-Vault-Access"].Count<=1
        &&MediaTypeHeaderValue.TryParse(Request.ContentType,out var media)&&media.Parameters.Count<=1
        &&media.Parameters.All(p=>string.Equals(p.Name,"charset",StringComparison.OrdinalIgnoreCase));

    [HttpPost("login")]
    public async Task<ActionResult<DataOutput<AuthenticationOutput?>>> Login([FromBody] LoginCommand command, CancellationToken cancellationToken)
    {
        var result = await commands.ExecuteCommandAsync<LoginCommand, AuthenticationOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: AuthenticationMessages.StatusCodes);
    }

    [HttpPost("2fa/verify")]
    public async Task<ActionResult<DataOutput<AuthenticationOutput?>>> VerifyChallenge([FromBody] VerifyChallengeCommand command, CancellationToken cancellationToken)
    {
        var result = await commands.ExecuteCommandAsync<VerifyChallengeCommand, AuthenticationOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: AuthenticationMessages.StatusCodes);
    }
}
