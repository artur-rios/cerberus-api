using System.Text.Json.Serialization;

namespace ArturRios.Cerberus.Domain.Accounts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EncryptedEnvelope(string Format, long KeyEpoch, string KeySalt, string Nonce, string Ciphertext, string Tag)
{
    public bool IsValid() => Format == "cerberus-content-v1" && KeyEpoch is > 0 and <= 9007199254740991
        && KeySalt is { Length: 43 } && Canonical(KeySalt, 32)
        && Nonce is { Length: 16 } && Canonical(Nonce, 12)
        && Tag is { Length: 22 } && Canonical(Tag, 16) && Canonical(Ciphertext, null);

    private static bool Canonical(string? value, int? size)
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            return false;
        try
        {
            var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
            return bytes.Length > 0 && (size is null || bytes.Length == size)
                && Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') == value;
        }
        catch (FormatException) { return false; }
    }
}
