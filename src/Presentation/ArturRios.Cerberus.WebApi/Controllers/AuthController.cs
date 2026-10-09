using ArturRios.Cerberus.Command.Authentication;
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
