namespace ArturRios.Cerberus.Domain.Protection;

public static class ProtocolBinary
{
    public const long MaxInteger = 9007199254740991;
    public static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static bool TryDecode(string? value, int? length, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value) || value.Length > 1048576
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return false;
        try
        {
            var decoded = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
            if (decoded.Length == 0 || length is not null && decoded.Length != length || Encode(decoded) != value) return false;
            bytes = decoded;
            return true;
        }
        catch (FormatException) { return false; }
    }
}
