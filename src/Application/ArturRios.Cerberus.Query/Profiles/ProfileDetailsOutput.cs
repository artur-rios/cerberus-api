using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Mediator.Query;
namespace ArturRios.Cerberus.Query.Profiles;
public sealed class ProfileDetailsOutput : QueryOutput
{
    public Guid ProfileId { get; init; }
    public long Revision { get; init; }
    public long ServerSequence { get; init; }
    public DateTimeOffset EditedAt { get; init; }
    public required EncryptedEnvelope Envelope { get; init; }
    public required ProfileKeyWrappers KeyWrappers { get; init; }
    public Guid[] RecordIds { get; init; } = [];
    public Guid[] FolderIds { get; init; } = [];
    public Guid[] CollectionIds { get; init; } = [];
}
