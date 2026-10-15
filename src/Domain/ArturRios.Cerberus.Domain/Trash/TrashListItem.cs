using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
namespace ArturRios.Cerberus.Domain.Trash;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TrashRecordItem), "record")]
[JsonDerivedType(typeof(TrashFolderItem), "folder")]
[JsonDerivedType(typeof(TrashCollectionItem), "collection")]
[JsonDerivedType(typeof(TrashProfileItem), "profile")]
public abstract record TrashListItem(Guid OperationId, Guid ResourceId, long Revision, long ServerSequence, DateTimeOffset EditedAt, DateTimeOffset DeletedAt, DateTimeOffset PurgeAt, EncryptedEnvelope Envelope);

public sealed record TrashRecordItem(Guid OperationId, Guid ResourceId, long Revision, long ServerSequence, DateTimeOffset EditedAt, DateTimeOffset DeletedAt, DateTimeOffset PurgeAt, EncryptedEnvelope Envelope) : TrashListItem(OperationId, ResourceId, Revision, ServerSequence, EditedAt, DeletedAt, PurgeAt, Envelope);
public sealed record TrashFolderItem(Guid OperationId, Guid ResourceId, long Revision, long ServerSequence, DateTimeOffset EditedAt, DateTimeOffset DeletedAt, DateTimeOffset PurgeAt, EncryptedEnvelope Envelope) : TrashListItem(OperationId, ResourceId, Revision, ServerSequence, EditedAt, DeletedAt, PurgeAt, Envelope);
public sealed record TrashCollectionItem(Guid OperationId, Guid ResourceId, long Revision, long ServerSequence, DateTimeOffset EditedAt, DateTimeOffset DeletedAt, DateTimeOffset PurgeAt, EncryptedEnvelope Envelope, long KeyEpoch) : TrashListItem(OperationId, ResourceId, Revision, ServerSequence, EditedAt, DeletedAt, PurgeAt, Envelope);
public sealed record TrashProfileItem(Guid OperationId, Guid ResourceId, long Revision, long ServerSequence, DateTimeOffset EditedAt, DateTimeOffset DeletedAt, DateTimeOffset PurgeAt, EncryptedEnvelope Envelope, ProfileKeyWrappers KeyWrappers) : TrashListItem(OperationId, ResourceId, Revision, ServerSequence, EditedAt, DeletedAt, PurgeAt, Envelope);
