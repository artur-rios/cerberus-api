using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;

namespace ArturRios.Cerberus.Data.Tests;

internal static class OfflineAuthorizationTestData
{
    internal sealed record State(long Owner, long Session, long Profile, Guid AccountPublicId, Guid ProfilePublicId, OfflineAuthorizationRequest Request,
        long ForeignOwner = 0, Guid ForeignOwnerPublicId = default, long Collection = 0, long Grant = 0, Guid GrantPublicId = default);
    internal static async Task<State> Setup(PostgresFixture fixture, bool sharing = false)
    {
        await using var db = fixture.CreateContext();
        var identity = Guid.NewGuid();
        var a = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = identity, Revision = 7, DetailsEnvelope = [11, 22] };
        db.Accounts.Add(a); await db.SaveChangesAsync();
        var p = new Profile { PublicId = Guid.NewGuid(), AccountId = a.Id, Revision = 3,
            Envelope = JsonSerializer.SerializeToUtf8Bytes(new EncryptedEnvelope("cerberus-content-v1", 2,
                ProtocolBinary.Encode(new byte[32]), ProtocolBinary.Encode(new byte[12]), "AQID", ProtocolBinary.Encode(new byte[16])), new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        db.Profiles.Add(p); await db.SaveChangesAsync();
        var verifier = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        var s = new VaultAccessSession { AccountId = a.Id, HandleVerifier = verifier, ProfileId = p.Id,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 };
        db.VaultAccessSessions.Add(s); await db.SaveChangesAsync();
        var result = new State(a.Id, s.Id, p.Id, a.PublicId, p.PublicId, new(identity, verifier, p.PublicId, 3));
        if (!sharing) return result;
        var owner = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid() };
        db.Accounts.Add(owner); await db.SaveChangesAsync();
        var c = new VaultCollection { PublicId = Guid.NewGuid(), AccountId = owner.Id, Revision = 2, KeyEpoch = 2 };
        db.Collections.Add(c); await db.SaveChangesAsync();
        var g = new CollectionGrant { PublicId = Guid.NewGuid(), CollectionId = c.Id, RecipientAccountId = a.Id, Revision = 4,
            State = CollectionGrantState.Active, Access = CollectionGrantAccess.ReadOnly };
        db.CollectionGrants.Add(g); db.ProfileCollections.Add(new ProfileCollection { ProfileId = p.Id, CollectionId = c.Id });
        await db.SaveChangesAsync();
        return result with { ForeignOwner = owner.Id, ForeignOwnerPublicId = owner.PublicId, Collection = c.Id, Grant = g.Id, GrantPublicId = g.PublicId };
    }
    internal sealed class Signer(Func<OfflineLeaseSnapshot, SignedOfflineLease?>? overrideSign = null) : IOfflineLeaseSigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public int Calls { get; private set; }
        public SignedOfflineLease? Sign(OfflineLeaseSnapshot s)
        { Calls++; return overrideSign is null ? OfflineLease.Sign(s, "fixture-lease-issuer", _key.ExportPkcs8PrivateKeyPem()) : overrideSign(s); }
        public void Dispose() => _key.Dispose();
    }
}
