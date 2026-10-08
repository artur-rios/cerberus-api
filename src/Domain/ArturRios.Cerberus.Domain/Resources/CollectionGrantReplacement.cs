using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Resources;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CollectionGrantReplacement(Guid GrantId,long ExpectedRevision,RecipientEnvelope KeyEnvelope);
