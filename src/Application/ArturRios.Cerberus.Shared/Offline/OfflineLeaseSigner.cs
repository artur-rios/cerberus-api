using System.Security.Cryptography;
using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Cerberus.Shared.Configuration;

namespace ArturRios.Cerberus.Shared.Offline;

public sealed class OfflineLeaseSigner(CerberusOptions options) : IOfflineLeaseSigner
{
    public SignedOfflineLease? Sign(OfflineLeaseSnapshot snapshot)
    {
        if (!OfflineLease.ValidIssuer(options.OfflineLeaseIssuer)
            || !OfflineLease.TrySigningPublicKey(options.OfflineLeasePrivateKeyPem, out _)) return null;
        try { return OfflineLease.Sign(snapshot, options.OfflineLeaseIssuer!, options.OfflineLeasePrivateKeyPem!); }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return null; }
    }
}
