using System.Text.Json.Serialization;

namespace ArturRios.Cerberus.Domain.Accounts;

[JsonPolymorphic(TypeDiscriminatorPropertyName="entryType")]
[JsonDerivedType(typeof(AccountExportAccount),"account")]
[JsonDerivedType(typeof(AccountExportProtection),"protection")]
[JsonDerivedType(typeof(AccountExportProfile),"profile")]
[JsonDerivedType(typeof(AccountExportRecord),"record")]
[JsonDerivedType(typeof(AccountExportFolder),"folder")]
[JsonDerivedType(typeof(AccountExportCollection),"collection")]
[JsonDerivedType(typeof(AccountExportRelationship),"relationship")]
[JsonDerivedType(typeof(AccountExportCollectionGrant),"collection-grant")]
[JsonDerivedType(typeof(AccountExportSharedReference),"shared-collection-reference")]
[JsonDerivedType(typeof(AccountExportSoftwareGrant),"software-grant")]
[JsonDerivedType(typeof(AccountExportSoftwareTarget),"software-target")]
[JsonDerivedType(typeof(AccountExportTrashOperation),"trash-operation")]
public abstract record AccountExportEntry;

public sealed record AccountExportAccount(byte[] Details):AccountExportEntry;
public sealed record AccountExportProtection(long Revision,long KeyEpoch,long RecoveryGeneration,byte[] Material):AccountExportEntry;
public sealed record AccountExportProfile(Guid ResourceId,long Revision,long ServerSequence,DateTimeOffset EditedAt,
    DateTimeOffset? DeletedAt,DateTimeOffset? PurgeAt,byte[] Envelope,byte[] KeyWrappers):AccountExportEntry;
public sealed record AccountExportRecord(Guid ResourceId,long Revision,long ServerSequence,DateTimeOffset EditedAt,
    DateTimeOffset? DeletedAt,DateTimeOffset? PurgeAt,byte[] Envelope,Guid? FolderId):AccountExportEntry;
public sealed record AccountExportFolder(Guid ResourceId,long Revision,long ServerSequence,DateTimeOffset EditedAt,
    DateTimeOffset? DeletedAt,DateTimeOffset? PurgeAt,byte[] Envelope,Guid? ParentFolderId):AccountExportEntry;
public sealed record AccountExportCollection(Guid ResourceId,long Revision,long ServerSequence,DateTimeOffset EditedAt,
    DateTimeOffset? DeletedAt,DateTimeOffset? PurgeAt,byte[] Envelope,long KeyEpoch):AccountExportEntry;
public sealed record AccountExportRelationship(string RelationshipKind,string SourceKind,Guid SourceId,string TargetKind,Guid TargetId,
    Guid? OperationId):AccountExportEntry;
public sealed record AccountExportCollectionGrant(Guid GrantId,Guid CollectionId,Guid RecipientAccountId,string Access,string State,
    long Revision,byte[] RecipientKeyEnvelope):AccountExportEntry;
public sealed record AccountExportSharedReference(Guid CollectionId,Guid OwnerAccountId,Guid GrantId,long GrantRevision,string Access):AccountExportEntry;
public sealed record AccountExportSoftwareGrant(Guid GrantId,Guid SoftwareId,string State,long Revision,byte[] RecipientKey):AccountExportEntry;
public sealed record AccountExportSoftwareTarget(Guid GrantId,string ResourceKind,Guid ResourceId,byte[] RecipientKeyEnvelope):AccountExportEntry;
public sealed record AccountExportTrashOperation(Guid OperationId,string RootKind,Guid RootId,DateTimeOffset DeletedAt,DateTimeOffset PurgeAt):AccountExportEntry;
