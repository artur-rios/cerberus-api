using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.TestSupport;

// Native client signing keys are generated only in tests, never in server code.
public sealed class ProtectionFixture : IDisposable
{
    public ECDsa Unlock { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _recovery = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _recipient = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _author = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public ProtectionMaterial Material { get; }
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    public static PublicJwk Public(ECDsa key)
    {
        var p = key.ExportParameters(false);
        return new("P-256", "EC", Encode(p.Q.X!), Encode(p.Q.Y!));
    }
    public ProtectionFixture()
    {
        var recovery = Public(_recovery);
        Material = new(new("cerberus-password-wrap-v1", 1, Encode(new byte[32]), Encode(new byte[12]), "AQID", Encode(new byte[16]),
                new("argon2id-v1.3", 65536, 3, 4, Encode(new byte[16]))),
            new("cerberus-recovery-wrap-v1", 1, Encode(new byte[32]), Encode(new byte[12]), "BAUG", Encode(new byte[16]), 1, recovery.Fingerprint()),
            Public(Unlock), recovery, Public(_recipient), Public(_author));
    }
    public ProtectionMaterial Rewrap(long epoch = 2) => Material with
    {
        PasswordWrapper = Material.PasswordWrapper with { KeyEpoch = epoch, KeySalt = Encode(RandomNumberGenerator.GetBytes(32)),
            Nonce = Encode(RandomNumberGenerator.GetBytes(12)), Ciphertext = "BwgJ", Kdf = Material.PasswordWrapper.Kdf with { Salt = Encode(RandomNumberGenerator.GetBytes(16)) } },
        RecoveryWrapper = Material.RecoveryWrapper with { KeyEpoch = epoch, KeySalt = Encode(RandomNumberGenerator.GetBytes(32)),
            Nonce = Encode(RandomNumberGenerator.GetBytes(12)), Ciphertext = "CgsM" }
    };
    public string SignRecovery(VaultProofChallenge challenge) => Encode(_recovery.SignData(VaultProof.SigningBytes(challenge), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    public string Sign(VaultProofChallenge challenge) => Encode(Unlock.SignData(VaultProof.SigningBytes(challenge), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    public void Dispose() { Unlock.Dispose(); _recovery.Dispose(); _recipient.Dispose(); _author.Dispose(); }
}
