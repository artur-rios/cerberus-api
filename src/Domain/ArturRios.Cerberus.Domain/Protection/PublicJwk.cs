using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ArturRios.Cerberus.Domain.Protection;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PublicJwk(string Crv, string Kty, string X, string Y)
{
    public bool IsValid()
    {
        if (Crv != "P-256" || Kty != "EC" || !ProtocolBinary.TryDecode(X, 32, out var x) || !ProtocolBinary.TryDecode(Y, 32, out var y)) return false;
        try { using var key = ECDsa.Create(Parameters(x, y)); return key.KeySize == 256; }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException) { return false; }
    }
    public string Fingerprint() => IsValid()
        ? ProtocolBinary.Encode(SHA256.HashData(Encoding.ASCII.GetBytes($"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{X}\",\"y\":\"{Y}\"}}"))) : string.Empty;
    public bool Verify(byte[] data, byte[] signature)
    {
        if (signature is not { Length: 64 } || !IsValid()) return false;
        ProtocolBinary.TryDecode(X, 32, out var x); ProtocolBinary.TryDecode(Y, 32, out var y);
        try
        {
            using var key = ECDsa.Create(Parameters(x, y));
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException) { return false; }
    }
    private static ECParameters Parameters(byte[] x, byte[] y) => new()
    { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } };
}
