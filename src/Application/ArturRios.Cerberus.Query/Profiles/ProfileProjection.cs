using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Query.Profiles;

internal static class ProfileProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict };
    internal static bool TryRead(ProfileListRow? row, Guid actor, out ProfileListItem? item)
    {
        item = null;
        if (row is null || row.ProfileId == Guid.Empty || row.Revision is <= 0 or > ProtocolBinary.MaxInteger
            || row.ServerSequence is <= 0 or > ProtocolBinary.MaxInteger || row.EditedAt == default
            || row.EditedAt.Offset != TimeSpan.Zero || row.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond != 0
            || row.Envelope is null || row.KeyWrappers is null) return false;
        EncryptedEnvelope? envelope; ProfileKeyWrappers? wrappers;
        try
        {
            envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope, Json);
            wrappers = JsonSerializer.Deserialize<ProfileKeyWrappers>(row.KeyWrappers, Json);
        }
        catch (JsonException) { return false; }
        if (envelope?.IsValid() != true || wrappers?.IsValid() != true || wrappers.MasterKeyWrapper.GrantId != row.ProfileId
            || wrappers.MasterKeyWrapper.RecipientIdentityId != actor || wrappers.MasterKeyWrapper.GrantRevision > row.Revision
            || wrappers.MasterKeyWrapper.KeyEpoch != envelope.KeyEpoch) return false;
        item = new(row.ProfileId, row.Revision, row.ServerSequence, row.EditedAt, envelope, wrappers);
        return true;
    }
}
