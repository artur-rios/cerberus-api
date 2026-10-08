using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Query.Accounts;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/accounts")]
public sealed class AccountController(CommandMediator commands, QueryMediator queries) : ControllerBase
{
    [HttpGet("me")]
    [ProducesResponseType(typeof(DataOutput<AccountOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<AccountOutput?>>> GetCurrent(
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count != 0
            || Request.Headers["X-Cerberus-Vault-Access"].Count > 1)
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var identityId))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        var result = await queries.ExecuteQueryAsync<GetAccountQuery, AccountOutput>(new GetAccountQuery(identityId, vaultAccess), cancellationToken);
        return result.ToActionResult(statusMap: AccountQueryMessages.StatusCodes);
    }

    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(DataOutput<RegisterAccountOutput>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(DataOutput<RegisterAccountOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<RegisterAccountOutput?>>> Register(
        [FromBody] RegisterAccountCommand command, CancellationToken cancellationToken)
    {
        var authorization = Request.Headers.Authorization;
        if (authorization.Count != 0)
        {
            var header = authorization.Count == 1 ? authorization[0] : null;
            command.SetIdentityProof(header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? header[7..] : string.Empty);
        }
        var result = await commands.ExecuteCommandAsync<RegisterAccountCommand, RegisterAccountOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: AccountMessages.StatusCodes);
    }
}
