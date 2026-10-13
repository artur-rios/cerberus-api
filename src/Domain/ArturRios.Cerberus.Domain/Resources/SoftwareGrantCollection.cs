namespace ArturRios.Cerberus.Domain.Resources;

public sealed class SoftwareGrantCollection
{
    public long AccountId { get; set; }
    public long GrantId { get; set; }
    public long CollectionId { get; set; }
    public byte[] RecipientKeyEnvelope { get; set; } = [];
}
