using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Cerberus.Domain.Accounts;

public sealed class RegistrationOperation : Entity
{
    public Guid OperationId { get; set; }
    public Guid AccountPublicId { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public Guid? CompletedIdentityId { get; set; }
}
