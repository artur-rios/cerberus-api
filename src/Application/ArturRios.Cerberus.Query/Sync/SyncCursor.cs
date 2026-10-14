using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Shared.Configuration;

namespace ArturRios.Cerberus.Query.Sync;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SyncContinuation(
    [property: JsonRequired] Guid Actor, [property: JsonRequired] Guid ProfileId,
    [property: JsonRequired] Guid StreamId, [property: JsonRequired] long WindowVersion,
    [property: JsonRequired] long After, [property: JsonRequired] long Boundary, [property: JsonRequired] int PageSize)
{
    public bool IsValid() => Actor != Guid.Empty && ProfileId != Guid.Empty && StreamId != Guid.Empty
        && WindowVersion is > 0 and <= ProtocolBinary.MaxInteger && After >= 0 && After <= Boundary
        && Boundary is >= 0 and <= ProtocolBinary.MaxInteger && PageSize > 0;
}

public sealed class SyncCursor
{
    private static readonly byte[] Purpose = "cerberus-sync-download-cursor-v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict };
    private readonly byte[] _key;
    public SyncCursor(CerberusOptions options) => _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.RegistrationFingerprintKey!), Purpose);

    public string Encode(SyncContinuation continuation)
    {
        if (continuation?.IsValid() != true) throw new ArgumentException("Invalid synchronization continuation.");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(continuation, Json);
        var encoded = new byte[28 + plaintext.Length];
        RandomNumberGenerator.Fill(encoded.AsSpan(0, 12));
        using var cipher = new AesGcm(_key, 16);
        cipher.Encrypt(encoded.AsSpan(0, 12), plaintext, encoded.AsSpan(28), encoded.AsSpan(12, 16), Purpose);
        return ProtocolBinary.Encode(encoded);
    }

    public bool TryDecode(string? cursor, out SyncContinuation? continuation)
    {
        continuation = null;
        if (cursor is null || cursor.Length > 2048 || !ProtocolBinary.TryDecode(cursor, null, out var encoded) || encoded.Length <= 28) return false;
        try
        {
            var plaintext = new byte[encoded.Length - 28];
            using var cipher = new AesGcm(_key, 16);
            cipher.Decrypt(encoded.AsSpan(0, 12), encoded.AsSpan(28), encoded.AsSpan(12, 16), plaintext, Purpose);
            var value = JsonSerializer.Deserialize<SyncContinuation>(plaintext, Json);
            if (value?.IsValid() != true) return false;
            continuation = value;
            return true;
        }
        catch (Exception e) when (e is CryptographicException or JsonException) { return false; }
    }
}
