using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Query.Accounts;
using ArturRios.Cerberus.Query.Offline;
using ArturRios.Cerberus.Command.Offline;
using ArturRios.Cerberus.WebApi.Middleware;
using System.Net.Http.Headers;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/accounts")]
public sealed class AccountController(CommandMediator commands, QueryMediator queries, TimeProvider clock) : ControllerBase
{
    [HttpDelete("me/permanent")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<AccountDeletionOutput>),StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(DataOutput<AccountDeletionOutput>),StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<AccountDeletionOutput?>>> DeleteAccount(
        [FromBody]DeleteAccountCommand command,CancellationToken cancellationToken)
    {
        if(Request.Query.Count!=0||Request.Headers["X-Cerberus-Vault-Access"].Count>1
            ||!MediaTypeHeaderValue.TryParse(Request.ContentType,out var media)||media.Parameters.Count>1
            ||media.Parameters.Any(p=>!string.Equals(p.Name,"charset",StringComparison.OrdinalIgnoreCase)))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if(!Guid.TryParse(User.FindFirst("id")?.Value,out var actor)||actor==Guid.Empty)
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        var issued=User.FindAll("iat").ToArray();
        if(issued.Length!=1||!long.TryParse(issued[0].Value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var issuedAt)
            ||!ArturRios.Cerberus.Domain.Accounts.FreshAuthentication.IsFresh(issuedAt,clock.GetUtcNow()))
            return Unauthorized(ProcessOutput.New.WithError("fresh_authentication_required"));
        command.SetContext(actor,issuedAt,Request.Headers.TryGetValue("X-Cerberus-Vault-Access",out var access)?access.ToString():null);
        return(await commands.ExecuteCommandAsync<DeleteAccountCommand,AccountDeletionOutput>(command,cancellationToken)).ToActionResult(statusMap:AccountDeletionMessages.StatusCodes);
    }

    [HttpPost("me/closure/cancel")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<AccountClosureCancellationOutput>),StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<AccountClosureCancellationOutput?>>> CancelClosure(
        [FromBody]CancelAccountClosureCommand command,CancellationToken cancellationToken)
    {
        if(Request.Query.Count!=0||Request.Headers["X-Cerberus-Vault-Access"].Count>1
            ||!MediaTypeHeaderValue.TryParse(Request.ContentType,out var media)||media.Parameters.Count>1
            ||media.Parameters.Any(p=>!string.Equals(p.Name,"charset",StringComparison.OrdinalIgnoreCase)))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if(!Guid.TryParse(User.FindFirst("id")?.Value,out var actor)||actor==Guid.Empty)
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        var issued=User.FindAll("iat").ToArray();
        if(issued.Length!=1||!long.TryParse(issued[0].Value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var issuedAt)
            ||!ArturRios.Cerberus.Domain.Accounts.FreshAuthentication.IsFresh(issuedAt,clock.GetUtcNow()))
            return Unauthorized(ProcessOutput.New.WithError("fresh_authentication_required"));
        command.SetContext(actor,issuedAt);
        return(await commands.ExecuteCommandAsync<CancelAccountClosureCommand,AccountClosureCancellationOutput>(command,cancellationToken))
            .ToActionResult(statusMap:AccountClosureCancellationMessages.StatusCodes);
    }

    [HttpPost("me/closure")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<AccountClosureOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<AccountClosureOutput?>>> RequestClosure(
        [FromBody] RequestAccountClosureCommand command, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || !MediaTypeHeaderValue.TryParse(Request.ContentType, out var media) || media.Parameters.Count > 1
            || media.Parameters.Any(p => !string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase)))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        var issued = User.FindAll("iat").ToArray();
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor) || actor == Guid.Empty)
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        if (issued.Length != 1 || !long.TryParse(issued[0].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var issuedAt)
            || !ArturRios.Cerberus.Domain.Accounts.FreshAuthentication.IsFresh(issuedAt, clock.GetUtcNow()))
            return Unauthorized(ProcessOutput.New.WithError("fresh_authentication_required"));
        command.SetContext(actor, issuedAt);
        return (await commands.ExecuteCommandAsync<RequestAccountClosureCommand, AccountClosureOutput>(command, cancellationToken))
            .ToActionResult(statusMap: AccountClosureMessages.StatusCodes);
    }

    [HttpPut("me/offline-policy")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<SetOfflinePolicyOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<SetOfflinePolicyOutput?>>> SetOfflinePolicy(
        [FromBody] SetOfflinePolicyCommand command, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || !MediaTypeHeaderValue.TryParse(Request.ContentType, out var media) || media.Parameters.Count > 1
            || media.Parameters.Any(p => !string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase)))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var identityId) || identityId == Guid.Empty)
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        command.SetContext(identityId,
            Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var access) ? access.ToString() : null);
        var result = await commands.ExecuteCommandAsync<SetOfflinePolicyCommand, SetOfflinePolicyOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: OfflinePolicyUpdateMessages.StatusCodes);
    }

    [HttpGet("me/offline-policy")]
    [ProducesResponseType(typeof(DataOutput<OfflinePolicyOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<OfflinePolicyOutput?>>> GetOfflinePolicy(
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            || Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count != 0
            || Request.Headers["X-Cerberus-Vault-Access"].Count > 1)
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var rawAccess) ? rawAccess.ToString() : null;
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var identityId) || identityId == Guid.Empty)
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        var result = await queries.ExecuteQueryAsync<GetOfflinePolicyQuery, OfflinePolicyOutput>(
            new GetOfflinePolicyQuery(identityId, vaultAccess), cancellationToken);
        return result.ToActionResult(statusMap: OfflinePolicyMessages.StatusCodes);
    }

    [HttpPut("me")]
    [ProducesResponseType(typeof(DataOutput<UpdateAccountOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<UpdateAccountOutput?>>> UpdateCurrent(
        [FromBody] UpdateAccountCommand command,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || Request.Headers["X-Cerberus-Vault-Access"].Count > 1)
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var identityId))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var rawAccess) ? rawAccess.ToString() : null;
        command.SetAccess(identityId, vaultAccess);
        var result = await commands.ExecuteCommandAsync<UpdateAccountCommand, UpdateAccountOutput>(command, cancellationToken);
        return result.ToActionResult(statusMap: UpdateAccountMessages.StatusCodes);
    }

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
        if (Request.Query.Count != 0 || HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            || Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count != 0
            || Request.Headers["X-Cerberus-Vault-Access"].Count > 1)
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        // MVC normalizes an empty header to null; preserve raw presence so only
        // an absent handle requests authentication, while malformed input is 400.
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var rawAccess) ? rawAccess.ToString() : null;
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
