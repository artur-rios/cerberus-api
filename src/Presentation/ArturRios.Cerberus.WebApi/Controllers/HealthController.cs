using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Output;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Cerberus.WebApi.Controllers;

[ApiController]
[Route("health")]
[Produces("application/json")]
public sealed class HealthController(OperationalHealthProbe probe) : ControllerBase
{
    [HttpGet("live"), AllowAnonymous, HealthEndpoint]
    [ProducesResponseType(typeof(HealthStatus), StatusCodes.Status200OK)]
    public IActionResult Live() => Ok(new HealthStatus("healthy"));

    [HttpGet("ready"), AllowAnonymous, HealthEndpoint]
    [ProducesResponseType(typeof(HealthStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(HealthStatus), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Ready(CancellationToken ct)
    {
        var result = await probe.CheckAsync(ct);
        return StatusCode(result.Status == "healthy" ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, new HealthStatus(result.Status));
    }

    [HttpGet("details"), HealthEndpoint, OperationalAuthentication]
    [ProducesResponseType(typeof(HealthDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProcessOutput), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(HealthDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Details(CancellationToken ct)
    {
        var result = await probe.CheckAsync(ct);
        return StatusCode(result.Status == "healthy" ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, result);
    }
}
