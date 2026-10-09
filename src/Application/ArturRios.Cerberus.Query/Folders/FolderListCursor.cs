using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Shared.Configuration;
namespace ArturRios.Cerberus.Query.Folders;

public sealed record FolderListContinuation(Guid Actor, string AccessVerifier, int PageSize, long After, long Boundary);
public sealed class FolderListCursor
{
    private static readonly byte[] Purpose = "cerberus-folder-list-cursor-v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, NumberHandling = JsonNumberHandling.Strict };
    private readonly byte[] key;
    public FolderListCursor(CerberusOptions options) => key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.RegistrationFingerprintKey!), Purpose);
    public string Encode(FolderListContinuation continuation)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(continuation, Json);
        var encoded = new byte[12 + 16 + plaintext.Length];
        RandomNumberGenerator.Fill(encoded.AsSpan(0, 12));
        using var cipher = new AesGcm(key, 16);
        cipher.Encrypt(encoded.AsSpan(0, 12), plaintext, encoded.AsSpan(28), encoded.AsSpan(12, 16), Purpose);
        return ProtocolBinary.Encode(encoded);
    }
    public bool TryDecode(string cursor, out FolderListContinuation? continuation)
    {
        continuation = null;
        if (cursor.Length > 2048 || !ProtocolBinary.TryDecode(cursor, null, out var encoded) || encoded.Length <= 28) return false;
        try
        {
            var plaintext = new byte[encoded.Length - 28];
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(encoded.AsSpan(0, 12), encoded.AsSpan(28), encoded.AsSpan(12, 16), plaintext, Purpose);
            var value = JsonSerializer.Deserialize<FolderListContinuation>(plaintext, Json);
            if (value is null || value.Actor == Guid.Empty || value.AccessVerifier is not { Length: 64 }
                || value.AccessVerifier.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || value.PageSize <= 0 || value.After <= 0 || value.After >= value.Boundary || value.Boundary > ProtocolBinary.MaxInteger) return false;
            continuation = value;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException) { return false; }
    }
}
