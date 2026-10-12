using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Software;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SoftwareRecipientKey(
    [property: JsonRequired] string Crv,
    [property: JsonRequired] string Kty,
    [property: JsonRequired] string X,
    [property: JsonRequired] string Y)
{
    public PublicJwk ToPublicJwk() => new(Crv, Kty, X, Y);
    public bool IsValid() => ToPublicJwk().IsValid();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SoftwareGrantTargetInput(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] RecipientEnvelope KeyEnvelope);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SoftwareGrantInput(
    [property: JsonRequired] Guid GrantId,
    [property: JsonRequired] Guid SoftwareIdentityId,
    [property: JsonRequired] long? ExpectedRevision,
    [property: JsonRequired] SoftwareRecipientKey RecipientKey,
    [property: JsonRequired] SoftwareGrantTargetInput[] Targets)
{
    public bool IsValid()
    {
        if (GrantId == Guid.Empty || SoftwareIdentityId == Guid.Empty
            || ExpectedRevision is not (null or > 0 and < ProtocolBinary.MaxInteger)
            || RecipientKey?.IsValid() != true || Targets is not { Length: > 0 }) return false;

        var fingerprint = RecipientKey.ToPublicJwk().Fingerprint();
        var revision = (ExpectedRevision ?? 0) + 1;
        var seen = new HashSet<(string, Guid)>();
        return Targets.All(target => target is not null && target.Kind is "record" or "collection"
            && target.Id != Guid.Empty && seen.Add((target.Kind, target.Id))
            && target.KeyEnvelope?.IsValid() == true && target.KeyEnvelope.GrantId == GrantId
            && target.KeyEnvelope.GrantRevision == revision
            && target.KeyEnvelope.RecipientIdentityId == SoftwareIdentityId
            && target.KeyEnvelope.RecipientKeyFingerprint == fingerprint);
    }
}

public sealed record SoftwareGrantRequest(Guid Actor, string AccessVerifier, SoftwareGrantInput Input);
public sealed record SoftwareGrantDetails(Guid GrantId, long Revision);

public interface ISoftwareGrantStore
{
    Task<VaultResult<SoftwareGrantDetails>> GrantAsync(SoftwareGrantRequest request, CancellationToken cancellationToken);
}
