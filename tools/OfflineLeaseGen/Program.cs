using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Offline;

if (args.Length != 1) throw new ArgumentException("Supply the public test artifact output path.");
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
OfflineLease.TrySigningPublicKey(key.ExportPkcs8PrivateKeyPem(), out var pin);
OfflineLease.TrySigningPublicKey(wrongKey.ExportPkcs8PrivateKeyPem(), out var wrongPin);
const string issuer = "cerberus-server-lease-fixture";
var cases = new[] { 86400L, 1L, 123456L, 0L }.Select(seconds =>
{
    var enabled = seconds != 0;
    var snapshot = new OfflineLeaseSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, 3, 4, 5,
        [new(Guid.Parse("00000001-0000-0000-0000-000000000001"), 6), new(Guid.Parse("00000002-0000-0000-0000-000000000002"), 7)],
        enabled, enabled ? TimeSpan.FromSeconds(seconds) : null, DateTimeOffset.FromUnixTimeSeconds(1000));
    var lease = OfflineLease.Sign(snapshot, issuer, key.ExportPkcs8PrivateKeyPem());
    return new
    {
        token = lease.Compact, now = 1000, seconds,
        expected = new
        {
            iss = issuer, aud = OfflineLease.Audience, sub = snapshot.IdentityId, accountId = snapshot.AccountId,
            scopeKind = "profile", scopeId = snapshot.ProfileId, keyEpoch = 2, protectionRevision = 3, policyRevision = 4,
            revocationGeneration = 5, grantRevisions = snapshot.GrantRevisions, renewalEnabled = enabled
        }
    };
}).ToArray();
File.WriteAllBytes(args[0], JsonSerializer.SerializeToUtf8Bytes(new { classification = "public-test-fixtures", issuer, pinnedJwk = pin, wrongJwk = wrongPin, cases }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
