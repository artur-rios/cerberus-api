using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Records;

namespace ArturRios.Cerberus.Domain.Sync;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SyncEditsInput([property: JsonRequired] SyncEditInput[] Edits)
{
    public bool IsValid(int maximum) => maximum > 0 && Edits is { Length: > 0 } && Edits.Length <= maximum
        && Edits.All(e => e?.IsValid() == true) && Edits.Select(e => e.OperationId).Distinct().Count() == Edits.Length;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "editType")]
[JsonDerivedType(typeof(SyncRecordCreate), "record-create")]
[JsonDerivedType(typeof(SyncRecordUpdate), "record-update")]
[JsonDerivedType(typeof(SyncFolderCreate), "folder-create")]
[JsonDerivedType(typeof(SyncFolderUpdate), "folder-update")]
[JsonDerivedType(typeof(SyncCollectionCreate), "collection-create")]
[JsonDerivedType(typeof(SyncCollectionUpdate), "collection-update")]
[JsonDerivedType(typeof(SyncProfileCreate), "profile-create")]
[JsonDerivedType(typeof(SyncProfileUpdate), "profile-update")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public abstract record SyncEditInput([property: JsonRequired] Guid OperationId)
{
    [JsonIgnore] public abstract string ResourceKind { get; }
    [JsonIgnore] public abstract string Action { get; }
    [JsonIgnore] public abstract Guid ResourceId { get; }
    [JsonIgnore] public abstract DateTimeOffset EditedAt { get; }
    public abstract bool IsValid();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SyncContentEditInput([property: JsonRequired] Guid ResourceId,
    [property: JsonRequired] EncryptedEnvelope Envelope, [property: JsonRequired] DateTimeOffset EditedAt)
{
    public bool IsValid() => ResourceId != Guid.Empty && Envelope?.IsValid() == true && EditedAt.Offset == TimeSpan.Zero
        && EditedAt.Ticks >= TimeSpan.TicksPerMicrosecond;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public abstract record SyncContentUpdate(Guid OperationId, [property: JsonRequired] SyncContentEditInput Input) : SyncEditInput(OperationId)
{
    [JsonIgnore] public override string Action => "update";
    [JsonIgnore] public override Guid ResourceId => Input?.ResourceId ?? Guid.Empty;
    [JsonIgnore] public override DateTimeOffset EditedAt => Input?.EditedAt ?? default;
    public override bool IsValid() => OperationId != Guid.Empty && Input?.IsValid() == true;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SyncRecordUpdate(Guid OperationId, SyncContentEditInput Input) : SyncContentUpdate(OperationId, Input)
{ [JsonIgnore] public override string ResourceKind => "record"; }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SyncFolderUpdate(Guid OperationId, SyncContentEditInput Input) : SyncContentUpdate(OperationId, Input)
{ [JsonIgnore] public override string ResourceKind => "folder"; }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SyncCollectionUpdate(Guid OperationId, SyncContentEditInput Input) : SyncContentUpdate(OperationId, Input)
{ [JsonIgnore] public override string ResourceKind => "collection"; }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SyncProfileUpdate(Guid OperationId, SyncContentEditInput Input) : SyncContentUpdate(OperationId, Input)
{ [JsonIgnore] public override string ResourceKind => "profile"; }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SyncRecordCreate(Guid OperationId, [property: JsonRequired] RecordCreateInput Input) : SyncEditInput(OperationId)
{
    [JsonIgnore] public override string ResourceKind => "record";
    [JsonIgnore] public override string Action => "create";
    [JsonIgnore] public override Guid ResourceId => Input?.RecordId ?? Guid.Empty;
    [JsonIgnore] public override DateTimeOffset EditedAt => Input?.EditedAt ?? default;
    public override bool IsValid() => OperationId != Guid.Empty && Input?.IsValid() == true;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SyncFolderCreate(Guid OperationId, [property: JsonRequired] FolderCreateInput Input) : SyncEditInput(OperationId)
{
    [JsonIgnore] public override string ResourceKind => "folder";
    [JsonIgnore] public override string Action => "create";
    [JsonIgnore] public override Guid ResourceId => Input?.FolderId ?? Guid.Empty;
    [JsonIgnore] public override DateTimeOffset EditedAt => Input?.EditedAt ?? default;
    public override bool IsValid() => OperationId != Guid.Empty && Input?.IsValid() == true;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SyncCollectionCreate(Guid OperationId, [property: JsonRequired] CollectionCreateInput Input) : SyncEditInput(OperationId)
{
    [JsonIgnore] public override string ResourceKind => "collection";
    [JsonIgnore] public override string Action => "create";
    [JsonIgnore] public override Guid ResourceId => Input?.CollectionId ?? Guid.Empty;
    [JsonIgnore] public override DateTimeOffset EditedAt => Input?.EditedAt ?? default;
    public override bool IsValid() => OperationId != Guid.Empty && Input?.IsValid() == true;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SyncProfileCreate(Guid OperationId, [property: JsonRequired] ProfileCreateInput Input) : SyncEditInput(OperationId)
{
    [JsonIgnore] public override string ResourceKind => "profile";
    [JsonIgnore] public override string Action => "create";
    [JsonIgnore] public override Guid ResourceId => Input?.ProfileId ?? Guid.Empty;
    [JsonIgnore] public override DateTimeOffset EditedAt => Input?.EditedAt ?? default;
    public override bool IsValid() => OperationId != Guid.Empty && Input?.IsValid() == true;
}

public static class OfflineEditOrder
{
    public static DateTimeOffset Normalize(DateTimeOffset time) => time.AddTicks(-(time.Ticks % TimeSpan.TicksPerMicrosecond));
    // A newly accepted equal-time operation receives a larger persisted native
    // sequence under the same resource lock. Exact operation replay never writes.
    public static bool ShouldApply(DateTimeOffset incoming, DateTimeOffset stored) => Normalize(incoming) >= stored;
}

public sealed record SyncEditOutcome(Guid OperationId, string ResourceKind, Guid ResourceId, string Status,
    long? Revision = null, long? ServerSequence = null, DateTimeOffset? EditedAt = null)
{
    public bool IsValid() => OperationId != Guid.Empty && ResourceId != Guid.Empty
        && ResourceKind is "record" or "folder" or "collection" or "profile"
        && Status switch
        {
            "applied" or "superseded" => Revision is { } revision && SyncResourceData.Safe(revision)
                && ServerSequence is { } sequence && SyncResourceData.Safe(sequence)
                && EditedAt is { } time && SyncResourceData.ValidTime(time),
            "denied" or "permanently-deleted" => Revision is null && ServerSequence is null && EditedAt is null,
            _ => false
        };
}
public sealed record SyncEditsRequest(Guid Actor, string AccessVerifier, SyncEditsInput Input);
public sealed record SyncEditsDetails(Guid AccountId, Guid? ProfileId, DateTimeOffset ServerTime, IReadOnlyList<SyncEditOutcome> Outcomes);
public sealed record SyncEditsResult(SyncEditsDetails? Details = null, string? Error = null);
public interface ISyncEditsStore
{
    Task<SyncEditsResult> UploadAsync(SyncEditsRequest request, CancellationToken ct);
}
