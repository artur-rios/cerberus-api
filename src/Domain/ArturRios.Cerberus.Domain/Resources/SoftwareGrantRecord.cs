namespace ArturRios.Cerberus.Domain.Resources;

public sealed class SoftwareGrantRecord
{
    public long AccountId { get; set; }
    public long GrantId { get; set; }
    public long RecordId { get; set; }
    public byte[] RecipientKeyEnvelope { get; set; } = [];
}
