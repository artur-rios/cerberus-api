using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

public partial class CollectionGrantRevocationStoreTests
{
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenConcurrentRevocations_WhenAccountPairIsHeld_ThenSerializeAndPreserveWinner(bool reciprocal)
    {
        using var s = await Setup();
        var other = await AssociationSetup.Items(fixture, s.Recipient);
        var otherGrant = await AssociationSetup.Grant(fixture, s.Recipient, s.RecipientKey, s.Owner, s.OwnerKey, other.Collection);
        var otherInput = new CollectionGrantRevocationInput(1);
        var reached = Signal(); var release = Signal(); var waiting = Signal(); int holder = 0, waiter = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var paused = new ProfileSetup.Factory(fixture, new After(c => Lock(c, "account"), async c => { holder = ((NpgsqlConnection)c.Connection!).ProcessID; reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }));
        var observed = new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), c => { waiter = ((NpgsqlConnection)c.Connection!).ProcessID; waiting.TrySetResult(); return Task.CompletedTask; }));
        var first = Revoke(s, factory: paused, ct: ct.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = reciprocal ? Store(observed).RevokeAsync(new(s.Recipient.Actor, s.Recipient.Verifier, other.Collection.PublicId, otherGrant.PublicId, otherInput), ct.Token) : Revoke(s, factory: observed, ct: ct.Token);
        try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Blocked(holder, waiter, ct.Token); Assert.False(second.IsCompleted); }
        finally { release.TrySetResult(); }
        Assert.Null((await first).Error); Assert.Equal(reciprocal ? "vault_access_denied" : "revision_conflict", (await second).Error);
        await using var db = fixture.CreateContext(); Assert.Equal(2, await Generation(s));
        Assert.Equal(2, (await db.CollectionGrants.SingleAsync(x => x.Id == s.Grant.Id)).Revision);
        Assert.Equal(1, (await db.CollectionGrants.SingleAsync(x => x.Id == otherGrant.Id)).Revision);
        Assert.Equal(1, (await db.Accounts.SingleAsync(x => x.Id == s.Owner.InternalId)).RevocationGeneration);
        if (reciprocal)
        {
            var fresh = await Unlock(s.Recipient, s.RecipientKey);
            Assert.Null((await Store().RevokeAsync(new(s.Recipient.Actor, fresh, other.Collection.PublicId, otherGrant.PublicId, otherInput), default)).Error);
        }
    }

    [FunctionalTheory][InlineData("lifecycle", false)][InlineData("lifecycle", true)][InlineData("protection", false)][InlineData("protection", true)][InlineData("unlock", false)][InlineData("unlock", true)]
    public async Task GivenRecipientWriterInEitherAcquisitionOrder_WhenRevoking_ThenSerializeUsingRealBlocking(string writer, bool changeFirst)
    {
        using var s = await Setup(); var raw = "{}"u8.ToArray(); ProtectionChangeRequest? protection = null; VaultProofChallenge? challenge = null;
        if (writer == "protection")
        {
            var input = new ProtectionChange(s.Recipient.AccountId, 1, 1, "rewrap", s.RecipientKey.Rewrap(), []); raw = Bytes(input);
            challenge = (await new VaultProtectionStore(fixture).ChallengeAsync(s.Recipient.Actor, "change-protection", ProtocolBinary.Encode(SHA256.HashData(raw)), default)).Data!;
            protection = new(s.Recipient.Actor, s.Recipient.Verifier, challenge.ChallengeId, s.RecipientKey.Sign(challenge), raw, input);
        }
        if (writer == "unlock") challenge = (await new VaultProtectionStore(fixture).ChallengeAsync(s.Recipient.Actor, ProtocolBinary.Encode(SHA256.HashData(raw)), default)).Data!;
        var reached = Signal(); var release = Signal(); var waiting = Signal(); int holder = 0, waiter = 0; string? issued = null;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<string?> Writer(IDbContextFactory<AppDbContext> factory)
        {
            if (writer == "protection") return (await new VaultProtectionChangeStore(factory).ChangeAsync(protection!, ct.Token)).Error;
            if (writer == "unlock")
            {
                var result = await new VaultProtectionStore(factory).UnlockAsync(s.Recipient.Actor, 1, challenge!.ChallengeId, s.RecipientKey.Sign(challenge), raw, ct.Token);
                if (result.Data is { } d) { Assert.True(OpaqueAccessHandle.TryHash(d.Access, out var verifier)); issued = verifier; }
                return result.Error;
            }
            await using var db = await factory.CreateDbContextAsync(ct.Token); await using var tx = await db.Database.BeginTransactionAsync(ct.Token);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.Recipient.InternalId} FOR UPDATE", ct.Token);
            await db.Accounts.Where(x => x.Id == s.Recipient.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending), ct.Token); await tx.CommitAsync(ct.Token); return null;
        }
        var paused = new ProfileSetup.Factory(fixture, new After(c => Lock(c, "account"), async c => { holder = ((NpgsqlConnection)c.Connection!).ProcessID; reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }));
        var observed = new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), c => { waiter = ((NpgsqlConnection)c.Connection!).ProcessID; waiting.TrySetResult(); return Task.CompletedTask; }));
        Task<VaultResult<CollectionGrantRevocationDetails>> change; Task<string?> write;
        if (changeFirst) { change = Revoke(s, factory: paused, ct: ct.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); write = Writer(observed); }
        else { write = Writer(paused); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); change = Revoke(s, factory: observed, ct: ct.Token); }
        try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Blocked(holder, waiter, ct.Token); Assert.False(changeFirst ? write.IsCompleted : change.IsCompleted); }
        finally { release.TrySetResult(); }
        var revocation = await change;
        Assert.Equal(!changeFirst && writer == "lifecycle" ? "not_found" : !changeFirst && writer == "protection" ? "revision_conflict" : null, revocation.Error);
        Assert.Equal(changeFirst && writer == "protection" ? "vault_access_denied" : changeFirst && writer == "unlock" ? "revision_conflict" : null, await write);
        Assert.Equal(writer == "protection" && !changeFirst ? 2 : writer == "lifecycle" && !changeFirst ? 1 : 2, await Generation(s));
        await using (var verify = fixture.CreateContext())
        {
            var saved = await verify.CollectionGrants.SingleAsync(x => x.Id == s.Grant.Id);
            Assert.Equal(revocation.Error is null ? 2 : 1, saved.Revision);
            Assert.Equal(revocation.Error is null ? CollectionGrantState.Revoked : CollectionGrantState.Active, saved.State);
            if (revocation.Error is null) Assert.Empty(saved.RecipientKeyEnvelope); else Assert.Equal(s.Grant.RecipientKeyEnvelope, saved.RecipientKeyEnvelope);
        }
        if (issued is not null) Assert.Equal("vault_access_denied", (await new CollectionReadStore(fixture).ReadAsync(new(s.Recipient.Actor, issued, s.PathCollection), default)).Error);
        if (writer == "unlock")
        {
            await using var db = fixture.CreateContext(); Assert.Equal(!changeFirst, (await db.VaultUnlockChallenges.SingleAsync(x => x.PublicId == challenge!.ChallengeId)).Consumed);
            Assert.Equal(changeFirst ? 1 : 2, await db.VaultAccessSessions.CountAsync(x => x.AccountId == s.Recipient.InternalId));
            var fresh = await Unlock(s.Recipient, s.RecipientKey); Assert.Equal("not_found", (await new CollectionReadStore(fixture).ReadAsync(new(s.Recipient.Actor, fresh, s.PathCollection), default)).Error);
        }
    }

    [FunctionalTheory]
    [InlineData("account", "recipient", "not_found")][InlineData("account", "pins", "revision_conflict")][InlineData("vault_access_session", "policy", "vault_access_denied")]
    [InlineData("profile", "selection", "not_found")][InlineData("collection", "trash", "not_found")][InlineData("collection_grant", "revoked", "revision_conflict")]
    [InlineData("collection_grant", "revision", "revision_conflict")][InlineData("account", "expiry", "vault_access_denied")][InlineData("vault_access_session", "expiry", "vault_access_denied")]
    [InlineData("profile", "expiry", "vault_access_denied")][InlineData("collection", "expiry", "vault_access_denied")][InlineData("collection_grant", "expiry", "vault_access_denied")]
    public async Task GivenStateLossDuringActualLockWait_WhenRevoking_ThenRecheckBeforeMutation(string table, string change, string error)
    {
        using var s = await Setup(); var profile = await AssociationSetup.Profile(fixture, s.Owner, s.OwnerKey);
        await Sql("INSERT INTO cerberus.profile_collection(profile_id,collection_id) VALUES({0},{1})", profile, s.Items.Collection.Id); s.Owner = s.Owner with { Verifier = await Selected(s.Owner, profile) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        if (change == "expiry") await Sql("UPDATE cerberus.vault_access_session SET expires_at={0} WHERE handle_verifier={1}", deadline, s.Owner.Verifier);
        var before = await Grants(s); await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        var id = table switch { "account" => s.Recipient.InternalId, "profile" => profile, "collection" => s.Items.Collection.Id, "collection_grant" => s.Grant.Id, _ => 0 };
        if (table == "vault_access_session") await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE handle_verifier={s.Owner.Verifier} FOR UPDATE");
        else { var lockSql = "SELECT FROM cerberus." + table + " WHERE id={0} FOR UPDATE"; await blocker.Database.ExecuteSqlRawAsync(lockSql, id); }
        var holder = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(); int waiter = 0; var reached = Signal(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = Revoke(s, factory: new ProfileSetup.Factory(fixture, new Before(c => Lock(c, table), c => { waiter = ((NpgsqlConnection)c.Connection!).ProcessID; reached.TrySetResult(); return Task.CompletedTask; })), ct: ct.Token);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Blocked(holder, waiter, ct.Token);
            if (change == "expiry") { var wait = deadline - DateTimeOffset.UtcNow; if (wait > TimeSpan.Zero) await Task.Delay(wait + TimeSpan.FromMilliseconds(100), ct.Token); }
            else await blocker.Database.ExecuteSqlRawAsync(change switch
            {
                "recipient" => "UPDATE cerberus.account SET state=1 WHERE id={0}",
                "pins" => "UPDATE cerberus.vault_protection SET material='bad'::bytea WHERE account_id={0}",
                "policy" => "UPDATE cerberus.vault_access_session SET policy_revision=2 WHERE account_id={0}",
                "selection" => "DELETE FROM cerberus.profile_collection WHERE profile_id={0}",
                "trash" => "UPDATE cerberus.collection SET deleted_at=statement_timestamp() WHERE id={0}",
                "revoked" => "UPDATE cerberus.collection_grant SET state=1 WHERE id={0}",
                _ => "UPDATE cerberus.collection_grant SET revision=revision+1 WHERE id={0}"
            }, change == "policy" ? s.Owner.InternalId : id);
        }
        finally { await tx.CommitAsync(); }
        Assert.Equal(error, (await pending).Error); Assert.Equal(1, await Generation(s));
        if (table != "collection_grant" || change == "expiry") Assert.Equal(before, await Grants(s));
    }

    [FunctionalFact]
    public async Task GivenLowerAccountHeld_WhenRevoking_ThenNoHigherAccountOrTargetLockIsHeldEarlyAndCancellationRollsBack()
    {
        using var s = await Setup(); var profile = await AssociationSetup.Profile(fixture, s.Owner, s.OwnerKey);
        await Sql("INSERT INTO cerberus.profile_collection(profile_id,collection_id) VALUES({0},{1})", profile, s.Items.Collection.Id);
        s.Owner = s.Owner with { Verifier = await Selected(s.Owner, profile) };
        var lower = s.Owner.AccountId.CompareTo(s.Recipient.AccountId) < 0 ? s.Owner : s.Recipient; var higher = lower == s.Owner ? s.Recipient : s.Owner;
        var before = await All(s); await using var blocker = fixture.CreateContext(); await using var held = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={lower.InternalId} FOR UPDATE");
        var holder = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(); int waiter = 0; var reached = Signal(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = Revoke(s, factory: new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), c => { waiter = ((NpgsqlConnection)c.Connection!).ProcessID; reached.TrySetResult(); return Task.CompletedTask; })), ct: ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Blocked(holder, waiter, ct.Token);
        await using var observer = fixture.CreateContext(); await using var tx = await observer.Database.BeginTransactionAsync();
        await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={higher.InternalId} FOR UPDATE NOWAIT");
        await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE handle_verifier={s.Owner.Verifier} FOR UPDATE NOWAIT");
        await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE id={profile} FOR UPDATE NOWAIT");
        await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE id={s.Items.Collection.Id} FOR UPDATE NOWAIT");
        await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_grant WHERE id={s.Grant.Id} FOR UPDATE NOWAIT");
        ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); await tx.RollbackAsync(); await held.RollbackAsync(); Assert.Equal(before, await All(s));
    }

    [FunctionalTheory][InlineData(0)][InlineData(24)]
    public async Task GivenUnrelatedInventory_WhenRevoking_ThenCaptureOnlyExactTargetAndLockCanonicalPairBeforeSessionProfileCollectionGrant(int inventory)
    {
        using var s = await Setup(); var profile = await AssociationSetup.Profile(fixture, s.Owner, s.OwnerKey);
        await Sql("INSERT INTO cerberus.profile_collection(profile_id,collection_id) VALUES({0},{1})", profile, s.Items.Collection.Id); s.Owner = s.Owner with { Verifier = await Selected(s.Owner, profile) };
        for (var i = 0; i < inventory; i++) { var other = await AssociationSetup.Items(fixture, s.Owner); await AssociationSetup.Grant(fixture, s.Owner, s.OwnerKey, s.Recipient, s.RecipientKey, other.Collection); }
        var capture = new Capture(); Assert.Equal("revision_conflict", (await Revoke(s, new(2), factory: new ProfileSetup.Factory(fixture, capture))).Error);
        Assert.Equal(5, capture.Locks.Count); Assert.Contains("ORDER BY public_id FOR UPDATE", capture.Locks[0]);
        Assert.Equal(new[] { s.Owner.AccountId, s.Recipient.AccountId }.Order(), Assert.Single(capture.Values[0].OfType<Guid[]>()).Order());
        Assert.Contains("vault_access_session", capture.Locks[1]); Assert.Contains("FOR NO KEY UPDATE", capture.Locks[2]); Assert.Contains("collection WHERE", capture.Locks[3]); Assert.Contains("collection_grant", capture.Locks[4]);
        Assert.Contains(s.Grant.Id, capture.Values[4]); Assert.Equal(0, capture.Writes); Assert.InRange(capture.Reads.Count, 1, 12);
        await using var check = fixture.CreateContext(); await check.Database.OpenConnectionAsync(); await using var explain = check.Database.GetDbConnection().CreateCommand();
        explain.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + capture.Snapshot; foreach (var p in capture.Parameters) explain.Parameters.Add(p.Clone());
        var planJson = (string)(await explain.ExecuteScalarAsync())!;
        using var plan = JsonDocument.Parse(planJson);
        output.WriteLine("PostgreSQL: " + await check.Database.SqlQuery<string>($"SELECT current_setting('server_version') AS \"Value\"").SingleAsync());
        output.WriteLine("EXPLAIN ANALYZE inventory=" + inventory + " SHA256=" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(planJson))));
        output.WriteLine(planJson);
        static IEnumerable<JsonElement> Nodes(JsonElement node)
        { yield return node; if (node.TryGetProperty("Plans", out var children)) foreach (var child in children.EnumerateArray()) foreach (var descendant in Nodes(child)) yield return descendant; }
        foreach (var name in new[] { "CTE target", "CTE grant_rows", "CTE recipient" })
        { var node = Assert.Single(Nodes(plan.RootElement[0].GetProperty("Plan")), x => x.TryGetProperty("Subplan Name", out var n) && n.GetString() == name); Assert.Equal(1d, node.GetProperty("Actual Rows").GetDouble()); output.WriteLine(name + ": Actual Rows=1"); }
        Assert.Null((await Revoke(s)).Error);
    }

    // Break: changing/revoking from a stale snapshot after an actual account lock wait.
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenPermissionChangeInEitherOrder_WhenRevoking_ThenExactlyOneWriterAdvancesGrantAndGeneration(bool revokeFirst)
    {
        using var s = await Setup(); var stable = await Stable(s);
        var input = new CollectionGrantChangeInput(1, "read-write", s.OwnerKey.WrapGrant(s.Owner.AccountId, s.PathCollection, s.PathGrant, s.Recipient.Actor, s.RecipientKey.Material.RecipientKey, 1, 2));
        var reached = Signal(); var release = Signal(); var waiting = Signal(); int holder = 0, waiter = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var paused = new ProfileSetup.Factory(fixture, new After(c => Lock(c, "account"), async c => { holder = ((NpgsqlConnection)c.Connection!).ProcessID; reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }));
        var observed = new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), c => { waiter = ((NpgsqlConnection)c.Connection!).ProcessID; waiting.TrySetResult(); return Task.CompletedTask; }));
        Task<VaultResult<CollectionGrantChangeDetails>> Change(IDbContextFactory<AppDbContext> f) => new CollectionGrantChangeStore(f).ChangeAsync(new(s.Owner.Actor, s.Owner.Verifier, s.PathCollection, s.PathGrant, input), ct.Token);
        Task<VaultResult<CollectionGrantRevocationDetails>> revoke; Task<VaultResult<CollectionGrantChangeDetails>> change;
        if (revokeFirst) { revoke = Revoke(s, factory: paused, ct: ct.Token); await reached.Task.WaitAsync(ct.Token); change = Change(observed); }
        else { change = Change(paused); await reached.Task.WaitAsync(ct.Token); revoke = Revoke(s, factory: observed, ct: ct.Token); }
        try { await waiting.Task.WaitAsync(ct.Token); await Blocked(holder, waiter, ct.Token); }
        finally { release.TrySetResult(); }
        Assert.Equal(revokeFirst ? null : "revision_conflict", (await revoke).Error);
        // UC-36 deliberately hides the no-longer-active grant when revocation won.
        Assert.Equal(revokeFirst ? "not_found" : null, (await change).Error);
        Assert.Equal(stable, await Stable(s)); Assert.Equal(2, await Generation(s));
        await using var db = fixture.CreateContext(); var saved = await db.CollectionGrants.SingleAsync(x => x.Id == s.Grant.Id);
        Assert.Equal(2, saved.Revision); Assert.Equal(revokeFirst ? CollectionGrantState.Revoked : CollectionGrantState.Active, saved.State);
        if (revokeFirst) Assert.Empty(saved.RecipientKeyEnvelope); else Assert.Equal(Bytes(input.KeyEnvelope), saved.RecipientKeyEnvelope);
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenStaleRegrantInEitherOrder_WhenRevoking_ThenPreserveSingleRevocationWinner(bool revokeFirst)
    {
        using var s = await Setup(); var stable = await Stable(s);
        var input = new CollectionGrantInput(s.PathGrant, s.Recipient.AccountId, 1, "read-only", s.OwnerKey.WrapGrant(s.Owner.AccountId, s.PathCollection, s.PathGrant, s.Recipient.Actor, s.RecipientKey.Material.RecipientKey, 1, 2));
        var reached = Signal(); var release = Signal(); var waiting = Signal(); int holder = 0, waiter = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var paused = new ProfileSetup.Factory(fixture, new After(c => Lock(c, "account"), async c => { holder = ((NpgsqlConnection)c.Connection!).ProcessID; reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }));
        var observed = new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), c => { waiter = ((NpgsqlConnection)c.Connection!).ProcessID; waiting.TrySetResult(); return Task.CompletedTask; }));
        Task<VaultResult<CollectionGrantDetails>> Grant(IDbContextFactory<AppDbContext> f) => new CollectionGrantStore(f).GrantAsync(new(s.Owner.Actor, s.Owner.Verifier, s.PathCollection, input), ct.Token);
        Task<VaultResult<CollectionGrantRevocationDetails>> revoke; Task<VaultResult<CollectionGrantDetails>> grant;
        if (revokeFirst) { revoke = Revoke(s, factory: paused, ct: ct.Token); await reached.Task.WaitAsync(ct.Token); grant = Grant(observed); }
        else { grant = Grant(paused); await reached.Task.WaitAsync(ct.Token); revoke = Revoke(s, factory: observed, ct: ct.Token); }
        try { await waiting.Task.WaitAsync(ct.Token); await Blocked(holder, waiter, ct.Token); }
        finally { release.TrySetResult(); }
        Assert.Null((await revoke).Error); Assert.Equal("revision_conflict", (await grant).Error);
        Assert.Equal(stable, await Stable(s)); Assert.Equal(2, await Generation(s));
        await using var db = fixture.CreateContext(); var saved = await db.CollectionGrants.SingleAsync(x => x.Id == s.Grant.Id);
        Assert.Equal(2, saved.Revision); Assert.Equal(CollectionGrantState.Revoked, saved.State); Assert.Empty(saved.RecipientKeyEnvelope);
    }

    [FunctionalFact]
    public async Task GivenCapturedRevocationOvertakenByRegrant_WhenAccountWaitEnds_ThenPreserveNewActiveRevision()
    {
        using var s = await Setup(); var captured = Signal(); var attempt = Signal(); var waiting = Signal(); var held = Signal(); var release = Signal();
        int holder = 0, waiter = 0; using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var delayed = new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), async c =>
        { captured.TrySetResult(); await attempt.Task.WaitAsync(ct.Token); waiter = ((NpgsqlConnection)c.Connection!).ProcessID; waiting.TrySetResult(); }));
        var stale = Revoke(s, factory: delayed, ct: ct.Token); await captured.Task.WaitAsync(ct.Token);
        Assert.Null((await Revoke(s)).Error);
        var input = new CollectionGrantInput(s.PathGrant, s.Recipient.AccountId, 2, "read-write", s.OwnerKey.WrapGrant(s.Owner.AccountId, s.PathCollection, s.PathGrant, s.Recipient.Actor, s.RecipientKey.Material.RecipientKey, 1, 3));
        var paused = new ProfileSetup.Factory(fixture, new After(c => Lock(c, "account"), async c =>
        { holder = ((NpgsqlConnection)c.Connection!).ProcessID; held.TrySetResult(); await release.Task.WaitAsync(ct.Token); }));
        var regrant = new CollectionGrantStore(paused).GrantAsync(new(s.Owner.Actor, s.Owner.Verifier, s.PathCollection, input), ct.Token);
        await held.Task.WaitAsync(ct.Token); attempt.TrySetResult();
        try { await waiting.Task.WaitAsync(ct.Token); await Blocked(holder, waiter, ct.Token); }
        finally { release.TrySetResult(); }
        Assert.Null((await regrant).Error); Assert.Equal("revision_conflict", (await stale).Error); Assert.Equal(2, await Generation(s));
        await using var db = fixture.CreateContext(); var saved = await db.CollectionGrants.SingleAsync(x => x.Id == s.Grant.Id);
        Assert.Equal(3, saved.Revision); Assert.Equal(CollectionGrantState.Active, saved.State); Assert.Equal(Bytes(input.KeyEnvelope), saved.RecipientKeyEnvelope);
    }

    [FunctionalTheory][InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task GivenProfileAssociationWriterInEitherOrder_WhenRevoking_ThenKeepExactSelectionAndRecipientGeneration(bool recipient, bool revokeFirst)
    {
        using var s = await Setup(); var actor = recipient ? s.Recipient : s.Owner;
        var profileId = await AssociationSetup.Profile(fixture, actor, recipient ? s.RecipientKey : s.OwnerKey);
        await Sql("INSERT INTO cerberus.profile_collection(profile_id,collection_id) VALUES({0},{1})", profileId, s.Items.Collection.Id);
        if (!recipient) s.Owner = s.Owner with { Verifier = await Selected(s.Owner, profileId) };
        await using var inspect = fixture.CreateContext(); var profile = await inspect.Profiles.AsNoTracking().SingleAsync(x => x.Id == profileId);
        var reached = Signal(); var release = Signal(); var waiting = Signal(); int holder = 0, waiter = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var paused = new ProfileSetup.Factory(fixture, new After(c => Lock(c, "account"), async c => { holder = ((NpgsqlConnection)c.Connection!).ProcessID; reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }));
        var observed = new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), c => { waiter = ((NpgsqlConnection)c.Connection!).ProcessID; waiting.TrySetResult(); return Task.CompletedTask; }));
        async Task<string?> Associate(IDbContextFactory<AppDbContext> f) => (await new ProfileAssociationStore(f).SetAsync(new(actor.Actor, actor.Verifier, profile.PublicId, new(1, [], [], [])), ct.Token)).Error;
        Task<VaultResult<CollectionGrantRevocationDetails>> revoke; Task<string?> association;
        if (revokeFirst) { revoke = Revoke(s, factory: paused, ct: ct.Token); await reached.Task.WaitAsync(ct.Token); association = Associate(observed); }
        else { association = Associate(paused); await reached.Task.WaitAsync(ct.Token); revoke = Revoke(s, factory: observed, ct: ct.Token); }
        try { await waiting.Task.WaitAsync(ct.Token); await Blocked(holder, waiter, ct.Token); }
        finally { release.TrySetResult(); }
        Assert.Equal(revokeFirst ? null : recipient ? "revision_conflict" : "not_found", (await revoke).Error);
        Assert.Equal(revokeFirst && recipient ? "vault_access_denied" : null, await association);
        Assert.Equal(revokeFirst ? 2 : 1, await Generation(s));
        await using var db = fixture.CreateContext(); var saved = await db.CollectionGrants.SingleAsync(x => x.Id == s.Grant.Id);
        Assert.Equal(revokeFirst ? 2 : 1, saved.Revision); Assert.Equal(revokeFirst ? CollectionGrantState.Revoked : CollectionGrantState.Active, saved.State);
        Assert.Equal(revokeFirst && recipient ? 1 : 2, (await db.Profiles.SingleAsync(x => x.Id == profileId)).Revision);
        Assert.Equal(revokeFirst && recipient, await db.ProfileCollections.AnyAsync(x => x.ProfileId == profileId && x.CollectionId == s.Items.Collection.Id));
    }

    private async Task Blocked(int holder, int waiter, CancellationToken ct)
    {
        await using var observer = fixture.CreateContext();
        while (!await observer.Database.SqlQuery<bool>($"SELECT {holder}=ANY(pg_blocking_pids({waiter})) AS \"Value\"").SingleAsync(ct)) await Task.Delay(20, ct);
    }
}
