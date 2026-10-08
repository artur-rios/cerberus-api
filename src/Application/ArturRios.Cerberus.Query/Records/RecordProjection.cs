using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
namespace ArturRios.Cerberus.Query.Records;

internal static class RecordProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict };
    internal static bool TryRead(RecordListRow? row, out RecordListItem? item)
    {
        item = null;
        if (row is null || row.RecordId == Guid.Empty || row.Revision is <= 0 or > ProtocolBinary.MaxInteger
            || row.ServerSequence is <= 0 or > ProtocolBinary.MaxInteger || row.EditedAt.Ticks < TimeSpan.TicksPerMicrosecond
            || row.EditedAt.Offset != TimeSpan.Zero || row.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond != 0
            || row.Envelope is null) return false;
        EncryptedEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope, Json); }
        catch (JsonException) { return false; }
        if (envelope?.IsValid() != true) return false;
        item = new(row.RecordId, row.Revision, row.ServerSequence, row.EditedAt, envelope);
        return true;
    }
}
