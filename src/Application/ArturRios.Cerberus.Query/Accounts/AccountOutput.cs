using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Query;

namespace ArturRios.Cerberus.Query.Accounts;

public sealed class AccountOutput : QueryOutput
{
    public Guid Id { get; init; }
    public long Revision { get; init; }
    public string State { get; init; } = string.Empty;
    public required EncryptedEnvelope Details { get; init; }
}
