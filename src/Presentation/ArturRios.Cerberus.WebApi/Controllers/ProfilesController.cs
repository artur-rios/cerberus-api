using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Query.Profiles;
using ArturRios.Mediator.Query;
using Microsoft.AspNetCore.Http.Features;
using System.Globalization;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Mvc;
namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("api/profiles")]
public sealed class ProfilesController(CommandMediator commands, QueryMediator queries):ControllerBase
{
    [HttpDelete("{id}")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<DeleteProfileOutput>),StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<DeleteProfileOutput?>>> Delete([FromRoute]string id,[FromBody]DeleteProfileCommand command,
        [FromHeader(Name="X-Cerberus-Vault-Access")]string? vaultAccess,CancellationToken cancellationToken)
    {
        if(Request.Query.Count!=0 || Request.Headers["X-Cerberus-Vault-Access"].Count>1
            || !Guid.TryParseExact(id,"D",out var profileId) || profileId==Guid.Empty || id!=profileId.ToString("D"))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if(!Guid.TryParse(User.FindFirst("id")?.Value,out var actor))return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess=Request.Headers.TryGetValue("X-Cerberus-Vault-Access",out var raw)?raw.ToString():null;
        command.SetContext(actor,vaultAccess,profileId);
        var result=await commands.ExecuteCommandAsync<DeleteProfileCommand,DeleteProfileOutput>(command,cancellationToken);
        return result.ToActionResult(statusMap:DeleteProfileMessages.StatusCodes);
    }

    [HttpPut("{id}")]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<UpdateProfileOutput>),StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<UpdateProfileOutput?>>> Update([FromRoute]string id,[FromBody]UpdateProfileCommand command,
        [FromHeader(Name="X-Cerberus-Vault-Access")]string? vaultAccess,CancellationToken cancellationToken)
    {
        if(Request.Query.Count!=0 || Request.Headers["X-Cerberus-Vault-Access"].Count>1
            || !Guid.TryParseExact(id,"D",out var profileId) || profileId==Guid.Empty || id!=profileId.ToString("D"))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if(!Guid.TryParse(User.FindFirst("id")?.Value,out var actor))return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess=Request.Headers.TryGetValue("X-Cerberus-Vault-Access",out var raw)?raw.ToString():null;
        command.SetContext(actor,vaultAccess,profileId);
        var result=await commands.ExecuteCommandAsync<UpdateProfileCommand,UpdateProfileOutput>(command,cancellationToken);
        return result.ToActionResult(statusMap:UpdateProfileMessages.StatusCodes);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(DataOutput<ProfileDetailsOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<ProfileDetailsOutput?>>> Get([FromRoute] string id,
        [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess, CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            || Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count != 0
            || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
            || !Guid.TryParseExact(id, "D", out var profileId) || profileId == Guid.Empty || id != profileId.ToString("D"))
            return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        vaultAccess = Request.Headers.TryGetValue("X-Cerberus-Vault-Access", out var rawAccess) ? rawAccess.ToString() : null;
        if (!Guid.TryParse(User.FindFirst("id")?.Value, out var actor))
            return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        var result = await queries.ExecuteQueryAsync<GetProfileQuery, ProfileDetailsOutput>(new(actor, vaultAccess, profileId), cancellationToken);
        return result.ToActionResult(statusMap: ProfileReadMessages.StatusCodes);
    }

    [HttpGet]
    [ProducesResponseType(typeof(DataOutput<ProfileListOutput>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<ProfileListOutput?>>> List(
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
        var result = await queries.ExecuteQueryAsync<ListProfilesQuery, ProfileListOutput>(new(actor, vaultAccess, size, cursor), cancellationToken);
        return result.ToActionResult(statusMap: ProfileListMessages.StatusCodes);
    }

    [HttpPost]
    [VaultProofBody]
    [ProducesResponseType(typeof(DataOutput<CreateProfileOutput>),StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProcessOutput),StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DataOutput<CreateProfileOutput?>>> Create([FromBody]CreateProfileCommand command,
        [FromHeader(Name="X-Cerberus-Vault-Access")]string? vaultAccess,CancellationToken cancellationToken)
    {
        if(Request.Query.Count!=0 || Request.Headers["X-Cerberus-Vault-Access"].Count>1)return BadRequest(ProcessOutput.New.WithError("validation_failed"));
        if(!Guid.TryParse(User.FindFirst("id")?.Value,out var actor))return Unauthorized(ProcessOutput.New.WithError("authentication_required"));
        vaultAccess=Request.Headers.TryGetValue("X-Cerberus-Vault-Access",out var raw)?raw.ToString():null;
        command.SetContext(actor,vaultAccess);
        var result=await commands.ExecuteCommandAsync<CreateProfileCommand,CreateProfileOutput>(command,cancellationToken);
        return result.ToActionResult(statusMap:ProfileMessages.StatusCodes);
    }
}
