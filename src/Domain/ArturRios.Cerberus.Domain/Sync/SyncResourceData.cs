using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Sync;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "resourceType")]
[JsonDerivedType(typeof(SyncProfileData), "profile")]
[JsonDerivedType(typeof(SyncRecordData), "record")]
[JsonDerivedType(typeof(SyncFolderData), "folder")]
[JsonDerivedType(typeof(SyncCollectionData), "collection")]
[JsonDerivedType(typeof(SyncGrantData), "grant")]
public abstract record SyncResourceData(Guid ResourceId, long Revision)
{
    [JsonIgnore] public abstract string ResourceKind { get; }
    public abstract bool IsValid(Guid actor, Guid profileId);
    protected bool ValidContext(Guid actor, Guid profileId) => actor != Guid.Empty && profileId != Guid.Empty && ResourceId != Guid.Empty && Safe(Revision);
    protected bool ValidContent(Guid actor, Guid profileId, long sequence, DateTimeOffset edited, EncryptedEnvelope? envelope) => ValidContext(actor, profileId)
        && Safe(sequence) && ValidTime(edited) && envelope?.IsValid() == true;
    public static bool Safe(long value) => value is > 0 and <= ProtocolBinary.MaxInteger;
    public static bool ValidTime(DateTimeOffset value) => value.Ticks >= TimeSpan.TicksPerMicrosecond
        && value.Offset == TimeSpan.Zero && value.Ticks % TimeSpan.TicksPerMicrosecond == 0;
}

public sealed record SyncProfileData(Guid ResourceId, long Revision, long ServerSequence, DateTimeOffset EditedAt,
    EncryptedEnvelope Envelope, ProfileKeyWrappers KeyWrappers, Guid[] RecordIds, Guid[] FolderIds, Guid[] CollectionIds) : SyncResourceData(ResourceId, Revision)
{
    [JsonIgnore] public override string ResourceKind => "profile";
    public override bool IsValid(Guid actor, Guid profileId) => ResourceId == profileId && ValidContent(actor, profileId, ServerSequence, EditedAt, Envelope)
        && KeyWrappers?.IsValid() == true && KeyWrappers.MasterKeyWrapper.GrantId == ResourceId
        && KeyWrappers.MasterKeyWrapper.RecipientIdentityId == actor && KeyWrappers.MasterKeyWrapper.GrantRevision <= Revision
        && KeyWrappers.MasterKeyWrapper.KeyEpoch == Envelope.KeyEpoch && ProfileAssociationInput.ValidIds(RecordIds)
        && ProfileAssociationInput.ValidIds(FolderIds) && ProfileAssociationInput.ValidIds(CollectionIds);
}
public sealed record SyncRecordData(Guid ResourceId, long Revision, long ServerSequence, DateTimeOffset EditedAt, EncryptedEnvelope Envelope) : SyncResourceData(ResourceId, Revision)
{
    [JsonIgnore] public override string ResourceKind => "record";
    public override bool IsValid(Guid actor, Guid profileId) => ValidContent(actor, profileId, ServerSequence, EditedAt, Envelope);
}
public sealed record SyncFolderData(Guid ResourceId, long Revision, long ServerSequence, DateTimeOffset EditedAt, EncryptedEnvelope Envelope) : SyncResourceData(ResourceId, Revision)
{
    [JsonIgnore] public override string ResourceKind => "folder";
    public override bool IsValid(Guid actor, Guid profileId) => ValidContent(actor, profileId, ServerSequence, EditedAt, Envelope);
}
public sealed record SyncCollectionData(Guid ResourceId, long Revision, long ServerSequence, DateTimeOffset EditedAt, EncryptedEnvelope Envelope, long KeyEpoch) : SyncResourceData(ResourceId, Revision)
{
    [JsonIgnore] public override string ResourceKind => "collection";
    public override bool IsValid(Guid actor, Guid profileId) => ValidContent(actor, profileId, ServerSequence, EditedAt, Envelope) && Safe(KeyEpoch) && KeyEpoch == Envelope.KeyEpoch;
}
public sealed record SyncGrantData(Guid ResourceId, long Revision, Guid CollectionId, RecipientEnvelope RecipientKeyEnvelope) : SyncResourceData(ResourceId, Revision)
{
    [JsonIgnore] public override string ResourceKind => "grant";
    public override bool IsValid(Guid actor, Guid profileId) => ValidContext(actor, profileId) && CollectionId != Guid.Empty
        && RecipientKeyEnvelope?.IsValid() == true && RecipientKeyEnvelope.GrantId == ResourceId
        && RecipientKeyEnvelope.GrantRevision == Revision && RecipientKeyEnvelope.RecipientIdentityId == actor;
}

public sealed record SyncChangeItem(long Sequence, string ResourceKind, Guid ResourceId, string EventType,
    SyncResourceData? Data = null, DateTimeOffset? DeletedAt = null)
{
    public bool IsValid(Guid actor, Guid profileId) => actor != Guid.Empty && profileId != Guid.Empty && SyncResourceData.Safe(Sequence)
        && ResourceId != Guid.Empty && ResourceKind is "profile" or "record" or "folder" or "collection" or "grant"
        && EventType switch
        {
            "upsert" => DeletedAt is null && Data?.IsValid(actor, profileId) == true && Data.ResourceId == ResourceId && Data.ResourceKind == ResourceKind,
            "removed" => Data is null && DeletedAt is null,
            "permanently-deleted" => Data is null && DeletedAt is { } at && SyncResourceData.ValidTime(at),
            _ => false
        };
}
