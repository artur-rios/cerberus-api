using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;

namespace ArturRios.Cerberus.Shared.Operations;

public enum OperationalAuthorization { Authorized, Unauthenticated, Forbidden, Unavailable }

public sealed class OperationalHealthAuthorizer(CerberusOptions options, HeimdallTokenValidator tokens,
    HeimdallApplicationTokenValidator applicationTokens, IHeimdallOperationalClient people, IHeimdallApplicationClient applications)
{
    public async Task<OperationalAuthorization> AuthorizeAsync(string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var person = await tokens.ValidateOperationalAsync(token);
        var application = person is null ? await applicationTokens.ValidateAsync(token) : null;
        var principal = person ?? application;
        if (principal is null) return OperationalAuthorization.Unauthenticated;
        if (!OperationalGrants.TryParse(person is null ? options.OperationalApplicationIds : options.OperationalPersonIds, out var subjects))
            return OperationalAuthorization.Forbidden;
        if (!subjects.Contains(Guid.Parse(principal.FindFirst("id")!.Value))) return OperationalAuthorization.Forbidden;
        if (person is not null)
            return await people.RevalidateOperationalAsync(token, ct) switch
            {
                HeimdallAuthorization.Authorized => OperationalAuthorization.Authorized,
                HeimdallAuthorization.Denied => OperationalAuthorization.Unauthenticated,
                _ => OperationalAuthorization.Unavailable
            };
        var current = await applications.AuthenticateAsync(token, ct);
        return current.Error is null && current.Identity is not null ? OperationalAuthorization.Authorized
            : current.Error == "authentication_required" ? OperationalAuthorization.Unauthenticated : OperationalAuthorization.Unavailable;
    }
}
