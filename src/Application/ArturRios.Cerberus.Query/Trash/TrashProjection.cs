using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
namespace ArturRios.Cerberus.Query.Trash;

internal static class TrashProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict };
    private static bool Safe(long n) => n is > 0 and <= ProtocolBinary.MaxInteger;
    private static bool Time(DateTimeOffset t) => t.Ticks >= TimeSpan.TicksPerMicrosecond && t.Offset == TimeSpan.Zero
        && t.Ticks % TimeSpan.TicksPerMicrosecond == 0;
    internal static bool TryRead(TrashListRow? row, Guid actor, out TrashListItem? item)
    {
        item = null;
        if (row is null || row.OperationId == Guid.Empty || row.ResourceId == Guid.Empty || !Safe(row.Position)
            || !Safe(row.Revision) || !Safe(row.ServerSequence) || !Time(row.EditedAt) || !Time(row.DeletedAt)
            || !Time(row.PurgeAt) || row.PurgeAt <= row.DeletedAt || row.PurgeAt - row.DeletedAt != TimeSpan.FromDays(30)
            || row.Envelope is null || row.Kind is not ("record" or "folder" or "collection" or "profile")
            || (row.Kind == "collection" ? row.KeyEpoch is null : row.KeyEpoch is not null)
            || (row.Kind == "profile" ? row.KeyWrappers is null : row.KeyWrappers is not null)) return false;
        try
        {
            var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope, Json);
            if (envelope?.IsValid() != true) return false;
            switch (row.Kind)
            {
                case "record": item = new TrashRecordItem(row.OperationId, row.ResourceId, row.Revision, row.ServerSequence,
                    row.EditedAt, row.DeletedAt, row.PurgeAt, envelope); break;
                case "folder": item = new TrashFolderItem(row.OperationId, row.ResourceId, row.Revision, row.ServerSequence,
                    row.EditedAt, row.DeletedAt, row.PurgeAt, envelope); break;
                case "collection":
                    if (row.KeyEpoch != envelope.KeyEpoch) return false;
                    item = new TrashCollectionItem(row.OperationId, row.ResourceId, row.Revision, row.ServerSequence,
                        row.EditedAt, row.DeletedAt, row.PurgeAt, envelope, row.KeyEpoch.Value); break;
                case "profile":
                    var keys = JsonSerializer.Deserialize<ProfileKeyWrappers>(row.KeyWrappers!, Json);
                    if (keys?.IsValid() != true || keys.MasterKeyWrapper.GrantId != row.ResourceId
                        || keys.MasterKeyWrapper.RecipientIdentityId != actor || keys.MasterKeyWrapper.GrantRevision > row.Revision
                        || keys.MasterKeyWrapper.KeyEpoch != envelope.KeyEpoch) return false;
                    item = new TrashProfileItem(row.OperationId, row.ResourceId, row.Revision, row.ServerSequence,
                        row.EditedAt, row.DeletedAt, row.PurgeAt, envelope, keys); break;
            }
            return item is not null;
        }
        catch (JsonException) { return false; }
    }
}
