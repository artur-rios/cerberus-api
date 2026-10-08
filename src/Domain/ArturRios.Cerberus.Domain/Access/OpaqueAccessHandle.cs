using System.Security.Cryptography;

namespace ArturRios.Cerberus.Domain.Access;

public static class OpaqueAccessHandle
{
    public static bool TryHash(string? token, out string verifier)
    {
        verifier = string.Empty;
        if (token is not { Length: 43 } || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return false;
        Span<byte> bytes = stackalloc byte[32];
        try
        {
            if (!Convert.TryFromBase64String(token.Replace('-', '+').Replace('_', '/') + "=", bytes, out var size) || size != 32
                || Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != token) return false;
            verifier = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
