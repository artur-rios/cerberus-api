using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Offline;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class OfflineAuthorizationStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory]
    [InlineData("1 month")][InlineData("20000000 days")][InlineData("0.5 seconds")]
    public async Task GivenCorruptOrUnsupportedSavedInterval_WhenRenewing_ThenFailClosedWithoutSigning(string interval)
    {
        var s = await OfflineAuthorizationTestData.Setup(fixture);
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET renewal_interval=CAST({interval} AS interval) WHERE id={s.Owner}");
        using var signer = new OfflineAuthorizationTestData.Signer();
        var result = await new OfflineAuthorizationStore(fixture, signer).RenewAsync(s.Request, default);
        Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Details); Assert.Equal(0, signer.Calls);
        Assert.Equal(1, await db.Accounts.Where(x => x.Id == s.Owner).Select(x => x.PolicyRevision).SingleAsync());
    }
    [FunctionalFact]
    public async Task GivenUnavailableDatabase_WhenRenewing_ThenReturn503WithoutSigning()
    {
        using var signer = new OfflineAuthorizationTestData.Signer();
        var result = await new OfflineAuthorizationStore(new UnavailableFactory(), signer).RenewAsync(new(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), 3), default);
        Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Details); Assert.Equal(0, signer.Calls);
    }
    [FunctionalFact]
    public async Task GivenCancelledCaller_WhenRenewing_ThenPropagateCancellation()
    {
        using var signer = new OfflineAuthorizationTestData.Signer(); using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfflineAuthorizationStore(fixture, signer).RenewAsync(new(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), 3), ct.Token));
    }
    private sealed class UnavailableFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=fixture;Username=fixture;Password=fixture;Timeout=1").Options);
    }
    [FunctionalTheory]
    [InlineData("active", true, null)][InlineData("write", true, null)]
    [InlineData("revoked", false, null)][InlineData("missing", false, null)]
    [InlineData("ownerClosing", false, null)][InlineData("ownerErased", false, null)]
    [InlineData("collectionDeleted", false, null)][InlineData("collectionErased", false, null)]
    [InlineData("grantErased", false, null)][InlineData("invalidAccess", false, null)]
    [InlineData("invalidRevision", false, "persistence_unavailable")]
    public async Task GivenAssociatedSharedCollection_WhenRenewing_ThenBindOnlyCurrentPermittedGrants(string state, bool visible, string? error)
    {
        var s = await OfflineAuthorizationTestData.Setup(fixture, sharing: true);
        await using var db = fixture.CreateContext();
        if (state == "write") await db.CollectionGrants.Where(x => x.Id == s.Grant).ExecuteUpdateAsync(x => x.SetProperty(g => g.Access, ArturRios.Cerberus.Domain.Resources.CollectionGrantAccess.ReadWrite));
        if (state == "revoked") await db.CollectionGrants.Where(x => x.Id == s.Grant).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, ArturRios.Cerberus.Domain.Resources.CollectionGrantState.Revoked));
        if (state == "missing") await db.CollectionGrants.Where(x => x.Id == s.Grant).ExecuteDeleteAsync();
        if (state == "ownerClosing") await db.Accounts.Where(x => x.Id == s.ForeignOwner).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
        if (state == "collectionDeleted") await db.Collections.Where(x => x.Id == s.Collection).ExecuteUpdateAsync(x => x.SetProperty(c => c.DeletedAt, DateTimeOffset.UtcNow));
        if (state == "invalidAccess") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.collection_grant SET access=99 WHERE id={s.Grant}");
        if (state == "invalidRevision") await db.CollectionGrants.Where(x => x.Id == s.Grant).ExecuteUpdateAsync(x => x.SetProperty(g => g.Revision, 0));
        if (state is "ownerErased" or "collectionErased" or "grantErased")
        {
            var kind = state == "ownerErased" ? "account" : state == "collectionErased" ? "collection" : "grant";
            var id = kind == "account" ? s.ForeignOwnerPublicId : kind == "grant" ? s.GrantPublicId : await db.Collections.Where(x => x.Id == s.Collection).Select(x => x.PublicId).SingleAsync();
            db.TerminalErasures.Add(new TerminalErasure { ResourceKind = kind, ResourceId = id, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
        }
        using var signer = new OfflineAuthorizationTestData.Signer();
        var result = await new OfflineAuthorizationStore(fixture, signer).RenewAsync(s.Request, default);
        Assert.Equal(error, result.Error);
        if (error is not null) Assert.Null(result.Details);
        else if (visible) Assert.Equal(new OfflineGrantRevision(s.GrantPublicId, 4), Assert.Single(result.Details!.Snapshot.GrantRevisions));
        else Assert.Empty(result.Details!.Snapshot.GrantRevisions);
        Assert.Equal(1, await db.Accounts.Where(x => x.Id == s.Owner).Select(x => x.PolicyRevision).SingleAsync());
        Assert.Equal(3, await db.Profiles.Where(x => x.Id == s.Profile).Select(x => x.Revision).SingleAsync());
    }
    [FunctionalTheory]
    [InlineData("active", null)][InlineData("selected", null)][InlineData("disabled", null)]
    [InlineData("missingAccount", "not_found")][InlineData("closing", "not_found")][InlineData("erased", "not_found")]
    [InlineData("missingProfile", "not_found")][InlineData("foreignProfile", "not_found")][InlineData("deletedProfile", "not_found")]
    [InlineData("erasedProfile", "not_found")][InlineData("otherSelection", "not_found")]
    [InlineData("missingSession", "vault_access_denied")][InlineData("revoked", "vault_access_denied")]
    [InlineData("expired", "vault_access_denied")][InlineData("future", "vault_access_denied")]
    [InlineData("policy", "vault_access_denied")][InlineData("generation", "vault_access_denied")]
    [InlineData("staleExpected", "revision_conflict")][InlineData("invalidEnvelope", "persistence_unavailable")]
    [InlineData("invalidEpoch", "persistence_unavailable")][InlineData("invalidPolicy", "persistence_unavailable")]
    [InlineData("signerUnavailable", "persistence_unavailable")]
    public async Task GivenCurrentAuthority_WhenRenewing_ThenIssueOnlyCurrentOwnedProfileAndPreserveState(string state, string? error)
    {
        var identity = Guid.NewGuid(); var profileId = Guid.NewGuid();
        var verifier = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        await using var db = fixture.CreateContext();
        var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = identity, Revision = 7, DetailsEnvelope = [11, 22],
            State = state == "closing" ? AccountState.ClosurePending : AccountState.Active,
            PolicyRevision = state == "policy" ? 2 : state == "invalidPolicy" ? 0 : 1, RevocationGeneration = state == "generation" ? 2 : 1,
            RenewalEnabled = state != "disabled", RenewalInterval = state == "disabled" ? null : TimeSpan.FromHours(24) };
        db.Accounts.Add(account); await db.SaveChangesAsync();
        var owner = account.Id;
        if (state == "foreignProfile")
        { var other = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid() }; db.Accounts.Add(other); await db.SaveChangesAsync(); owner = other.Id; }
        var profile = new Profile { AccountId = owner, PublicId = profileId, Revision = 3,
            DeletedAt = state == "deletedProfile" ? DateTimeOffset.UtcNow : null,
            PurgeAt = state == "deletedProfile" ? DateTimeOffset.UtcNow.AddDays(30) : null,
            Envelope = state == "invalidEnvelope" ? [1, 2] : JsonSerializer.SerializeToUtf8Bytes(new EncryptedEnvelope("cerberus-content-v1", state == "invalidEpoch" ? 0 : 2,
                ProtocolBinary.Encode(new byte[32]), ProtocolBinary.Encode(new byte[12]), "AQID", ProtocolBinary.Encode(new byte[16])), new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        db.Profiles.Add(profile); await db.SaveChangesAsync();
        if (state != "missingSession") db.VaultAccessSessions.Add(new VaultAccessSession { AccountId = account.Id, HandleVerifier = verifier,
            ProfileId = state == "selected" ? profile.Id : state == "otherSelection" ? long.MaxValue : null,
            IssuedAt = state == "future" ? DateTimeOffset.UtcNow.AddMinutes(5) : DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = state == "disabled" ? null : state == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddHours(1),
            PolicyRevision = state == "invalidPolicy" ? 0 : 1, RevocationGeneration = 1, Revoked = state == "revoked" });
        if (state is "erased" or "erasedProfile") db.TerminalErasures.Add(new TerminalErasure
        { ResourceId = state == "erased" ? account.PublicId : profileId, ResourceKind = state == "erased" ? "account" : "profile", DeletedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        using var signer = new Signer(state == "signerUnavailable");
        var result = await new OfflineAuthorizationStore(fixture, signer).RenewAsync(new(state == "missingAccount" ? Guid.NewGuid() : identity, verifier,
            state == "missingProfile" ? Guid.NewGuid() : profileId, state == "staleExpected" ? 2 : 3), default);
        Assert.Equal(error, result.Error);
        if (error is null)
        {
            var d = result.Details!; Assert.Equal(identity, d.Snapshot.IdentityId); Assert.Equal(profileId, d.Snapshot.ProfileId);
            Assert.Equal(2, d.Snapshot.KeyEpoch); Assert.Equal(3, d.Snapshot.ProtectionRevision);
            Assert.True(d.Lease.MatchesMetadata(d.Snapshot)); Assert.Empty(d.Snapshot.GrantRevisions);
            Assert.Equal(state != "disabled", d.Snapshot.RenewalEnabled);
            Assert.Equal(state == "disabled", d.Lease.ExpiresAt is null);
        }
        else Assert.Null(result.Details);
        var saved = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == account.Id);
        Assert.Equal(7, saved.Revision); Assert.Equal(account.PolicyRevision, saved.PolicyRevision);
        Assert.Equal(account.RevocationGeneration, saved.RevocationGeneration); Assert.Equal(new byte[] { 11, 22 }, saved.DetailsEnvelope);
        Assert.Equal(3, await db.Profiles.Where(x => x.Id == profile.Id).Select(x => x.Revision).SingleAsync());
        Assert.Equal(error is null || state == "signerUnavailable" ? 1 : 0, signer.Calls);
    }
    private sealed class Signer(bool unavailable = false) : IOfflineLeaseSigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public int Calls { get; private set; }
        public SignedOfflineLease? Sign(OfflineLeaseSnapshot s) { Calls++; return unavailable ? null : OfflineLease.Sign(s, "fixture-lease-issuer", _key.ExportPkcs8PrivateKeyPem()); }
        public void Dispose() => _key.Dispose();
    }
}
