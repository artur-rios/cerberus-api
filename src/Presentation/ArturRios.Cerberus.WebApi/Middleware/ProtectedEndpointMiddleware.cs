using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Output;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.AspNetCore.Authorization;

namespace ArturRios.Cerberus.WebApi.Middleware;

public sealed class ProtectedEndpointMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, HeimdallTokenValidator tokens, IHeimdallClient identity, HeimdallApplicationTokenValidator applications)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || endpoint.Metadata.GetMetadata<ArturRios.Util.WebApi.Security.Attributes.AllowAnonymousAttribute>() is not null)
        {
            await next(context);
            return;
        }
        var headers = context.Request.Headers.Authorization;
        var header = headers.Count == 1 ? headers[0] : null;
        var token = header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? header[7..] : string.Empty;
        if (endpoint.Metadata.GetMetadata<OperationalAuthenticationAttribute>() is not null)
        {
            var operational = await context.RequestServices.GetRequiredService<OperationalHealthAuthorizer>().AuthorizeAsync(token, context.RequestAborted);
            if (operational != OperationalAuthorization.Authorized)
            {
                var (status, code) = operational switch
                {
                    OperationalAuthorization.Forbidden => (StatusCodes.Status403Forbidden, "operational_access_required"),
                    OperationalAuthorization.Unavailable => (StatusCodes.Status503ServiceUnavailable, "identity_unavailable"),
                    _ => (StatusCodes.Status401Unauthorized, "authentication_required")
                };
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(ProcessOutput.New.WithError(code), context.RequestAborted);
                return;
            }
            await next(context);
            return;
        }
        if (endpoint.Metadata.GetMetadata<ApplicationAuthenticationAttribute>() is not null)
        {
            var application = await applications.ValidateAsync(token);
            if (application is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(ProcessOutput.New.WithError("authentication_required"), context.RequestAborted);
                return;
            }
            context.User = application;
            await next(context);
            return;
        }
        var principal = await tokens.ValidateAsync(token);
        var authorization = principal is null ? HeimdallAuthorization.Denied
            : await identity.RevalidateAsync(token, context.RequestAborted);
        if (principal is null || authorization != HeimdallAuthorization.Authorized)
        {
            var unavailable = authorization == HeimdallAuthorization.Unavailable;
            context.Response.StatusCode = unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(ProcessOutput.New.WithError(unavailable ? "identity_unavailable" : "authentication_required"), context.RequestAborted);
            return;
        }
        context.User = principal;
        await next(context);
    }
}
