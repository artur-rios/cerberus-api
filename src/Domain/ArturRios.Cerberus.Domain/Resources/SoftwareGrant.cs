using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Cerberus.Domain.Resources;

public enum SoftwareGrantState { Active, Revoked }

public sealed class SoftwareGrant : VersionedEntity
{
    public Guid PublicId { get; set; }
    public long AccountId { get; set; }
    public Guid HeimdallSoftwarePublicId { get; set; }
    public byte[] RecipientKey { get; set; } = [];
    public SoftwareGrantState State { get; set; }
    public long Revision { get; set; } = 1;
}
