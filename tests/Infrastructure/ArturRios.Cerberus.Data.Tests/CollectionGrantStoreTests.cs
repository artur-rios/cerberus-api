using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class CollectionGrantStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory]
    [InlineData("read-only", false)][InlineData("read-write", false)][InlineData("read-only", true)]
    public async Task GivenCurrentOwnerAndRecipient_WhenGranting_ThenInsertOrRegrantExactlyOneRow(string access, bool regrant)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client);
        var a = await AssociationSetup.Items(fixture, s); await Members(s, a);
        CollectionGrant? old = null;
        if (regrant)
        {
            old = await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection);
            await Sql("UPDATE cerberus.collection_grant SET state=1,revision=4,recipient_key_envelope='bad'::bytea WHERE id={0}", old.Id);
        }
        var input = Input(s, owner, r, client, a.Collection, access, old?.PublicId, regrant ? 4 : null);
        var before = await Stable(s, r, a.Collection);
        var result = await Grant(s, a.Collection, input);
        Assert.Null(result.Error); Assert.NotNull(result.Data);
        Assert.Equal(a.Collection.PublicId, result.Data.CollectionId); Assert.Equal(input.GrantId, result.Data.GrantId);
        Assert.Equal(regrant ? 5 : 1, result.Data.Revision); Assert.Equal(access, result.Data.Access);
        Assert.Equal(new[] { a.Record.PublicId }, result.Data.RecordIds); Assert.Equal(new[] { a.Folder.PublicId }, result.Data.FolderIds);
        Assert.Equal(before, await Stable(s, r, a.Collection));
        await using var db = fixture.CreateContext();
        var row = Assert.Single(await db.CollectionGrants.Where(x => x.CollectionId == a.Collection.Id).ToArrayAsync());
        Assert.Equal(input.GrantId, row.PublicId); Assert.Equal(r.InternalId, row.RecipientAccountId);
        Assert.Equal(CollectionGrantState.Active, row.State); Assert.Equal(regrant ? 5 : 1, row.Revision);
        Assert.Equal(access == "read-only" ? CollectionGrantAccess.ReadOnly : CollectionGrantAccess.ReadWrite, row.Access);
        Assert.Equal(Bytes(input.KeyEnvelope), row.RecipientKeyEnvelope); Assert.NotEqual(Guid.Empty, row.ConcurrencyStamp);
        if (old is not null) { Assert.Equal(old.Id, row.Id); Assert.NotEqual(old.ConcurrencyStamp, row.ConcurrencyStamp); }
        Assert.Empty(await db.ProfileCollections.Where(x => x.CollectionId == a.Collection.Id).ToArrayAsync());
        Assert.Null((await new CollectionReadStore(fixture).ReadAsync(new(r.Actor, r.Verifier, a.Collection.PublicId), default)).Error);
    }

    [FunctionalTheory]
    [InlineData("Master", true)][InlineData("PerProfile", true)][InlineData("Master", false)][InlineData("PerProfile", false)]
    public async Task GivenSelectedOwner_WhenGranting_ThenRequireExactCurrentCollectionLink(string mode, bool linked)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); using var scoped = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var input = ProfileSetup.Input(s, owner, scoped, mode) with { CollectionIds = linked ? [a.Collection.PublicId] : [] };
        Assert.Null((await new ArturRios.Cerberus.Data.Profiles.ProfileCreateStore(fixture).CreateAsync(new(s.Actor, s.Verifier, input), default)).Error);
        await using var db = fixture.CreateContext(); var p = await db.Profiles.SingleAsync(x => x.PublicId == input.ProfileId);
        s = s with { Verifier = await Selected(s, p.Id) };
        var before = await Stable(s, r, a.Collection); var result = await Grant(s, a.Collection, Input(s, owner, r, client, a.Collection));
        Assert.Equal(linked ? null : "not_found", result.Error); Assert.Equal(before, await Stable(s, r, a.Collection));
    }

    [FunctionalTheory]
    [InlineData("missing", "not_found")][InlineData("closing", "not_found")][InlineData("terminal", "not_found")]
    [InlineData("unprotected", "not_found")][InlineData("self", "validation_failed")][InlineData("noSession", null)]
    [InlineData("ownerClosing", "not_found")][InlineData("ownerTerminal", "not_found")][InlineData("collectionTrash", "not_found")]
    [InlineData("collectionTerminal", "not_found")][InlineData("revoked", "vault_access_denied")][InlineData("expired", "vault_access_denied")]
    [InlineData("future", "vault_access_denied")][InlineData("policy", "vault_access_denied")][InlineData("generation", "vault_access_denied")]
    public async Task GivenRecipientOrAuthorityState_WhenGranting_ThenEnforceCurrentLifecycle(string mode, string? error)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var input = Input(s, owner, r, client, a.Collection);
        if (mode == "missing") input = input with { RecipientAccountId = Guid.NewGuid() };
        if (mode == "self") input = input with { RecipientAccountId = s.AccountId };
        if (mode is "closing" or "ownerClosing") await Sql("UPDATE cerberus.account SET state={0} WHERE id={1}", (int)AccountState.ClosurePending, mode == "closing" ? r.InternalId : s.InternalId);
        if (mode is "terminal" or "ownerTerminal" or "collectionTerminal") await Terminal(mode == "collectionTerminal" ? "collection" : "account", mode == "terminal" ? r.AccountId : mode == "ownerTerminal" ? s.AccountId : a.Collection.PublicId);
        if (mode == "unprotected") await Sql("DELETE FROM cerberus.vault_protection WHERE account_id={0}", r.InternalId);
        if (mode == "noSession") await Sql("DELETE FROM cerberus.vault_access_session WHERE account_id={0}", r.InternalId);
        if (mode == "collectionTrash") await Sql("UPDATE cerberus.collection SET deleted_at=statement_timestamp() WHERE id={0}", a.Collection.Id);
        if (mode is "revoked" or "expired" or "future" or "policy" or "generation") await Sql("UPDATE cerberus.vault_access_session SET " + (mode switch { "revoked" => "revoked=true", "expired" => "expires_at=statement_timestamp()", "future" => "issued_at=statement_timestamp()+interval '1 hour'", "policy" => "policy_revision=2", _ => "revocation_generation=2" }) + " WHERE handle_verifier={0}", s.Verifier);
        await Outcome(s, r, a.Collection, input, error);
    }

    [FunctionalTheory]
    [InlineData("active")][InlineData("activeSame")][InlineData("stale")][InlineData("missingExpected")][InlineData("wrongId")]
    [InlineData("noPairExpected")][InlineData("collision")][InlineData("terminal")][InlineData("revokedTerminal")]
    [InlineData("zero")][InlineData("unsafe")][InlineData("exhausted")][InlineData("invalidState")]
    public async Task GivenReservedOrNonmatchingGrantState_WhenGranting_Then409AndPreserveEveryRow(string mode)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var g = mode is "noPairExpected" or "terminal" ? null : await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection);
        if (g is not null && mode is not ("active" or "activeSame" or "collision")) await Sql("UPDATE cerberus.collection_grant SET state=1,revision=4 WHERE id={0}", g.Id);
        if (mode == "collision") { var other = await AssociationSetup.Items(fixture, s); await Sql("UPDATE cerberus.collection_grant SET collection_id={0} WHERE id={1}", other.Collection.Id, g!.Id); }
        if (mode is "zero" or "unsafe" or "exhausted") await Sql("UPDATE cerberus.collection_grant SET revision={0} WHERE id={1}", mode == "zero" ? 0L : mode == "unsafe" ? ProtocolBinary.MaxInteger + 1 : ProtocolBinary.MaxInteger, g!.Id);
        if (mode == "invalidState") await Sql("UPDATE cerberus.collection_grant SET state=99 WHERE id={0}", g!.Id);
        var input = Input(s, owner, r, client, a.Collection, id: mode is "active" or "wrongId" ? null : g?.PublicId,
            expected: mode is "active" or "activeSame" or "missingExpected" or "collision" or "terminal" ? null : mode == "stale" ? 3 : 4);
        if (mode is "terminal" or "revokedTerminal") await Terminal("grant", input.GrantId);
        await Outcome(s, r, a.Collection, input, "revision_conflict");
    }

    [FunctionalTheory]
    [InlineData("ownerPins")][InlineData("recipientPins")][InlineData("ownerMissing")][InlineData("ownerRevision")]
    [InlineData("recipientRevision")][InlineData("recipientEpoch")][InlineData("recipientGeneration")]
    [InlineData("collectionEnvelope")][InlineData("collectionEpoch")][InlineData("collectionPurge")]
    public async Task GivenRequiredStoredCorruption_WhenGranting_Then503BeforeCallerBinding(string mode)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        if (mode == "ownerMissing") await Sql("DELETE FROM cerberus.vault_protection WHERE account_id={0}", s.InternalId);
        else if (mode.StartsWith("collection")) await Sql("UPDATE cerberus.collection SET " + (mode == "collectionEnvelope" ? "envelope='bad'::bytea" : mode == "collectionEpoch" ? "key_epoch=2" : "purge_at=statement_timestamp()") + " WHERE id={0}", a.Collection.Id);
        else await Sql("UPDATE cerberus.vault_protection SET " + (mode.EndsWith("Pins") ? "material='bad'::bytea" : mode.EndsWith("Revision") ? "revision=0" : mode.EndsWith("Epoch") ? "key_epoch=2" : "recovery_generation=2") + " WHERE account_id={0}", mode.StartsWith("owner") ? s.InternalId : r.InternalId);
        await Outcome(s, r, a.Collection, Input(s, owner, r, client, a.Collection), "persistence_unavailable");
    }

    [FunctionalTheory]
    [InlineData("owner")][InlineData("collection")][InlineData("epoch")][InlineData("identity")]
    [InlineData("recipientPin")][InlineData("authorPin")][InlineData("signature")]
    public async Task GivenShapeValidUnboundCallerEnvelope_WhenGranting_Then400AndNoMutation(string mode)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); using var other = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var input = Input(s, owner, r, client, a.Collection);
        var wrap = owner.WrapGrant(mode == "owner" ? Guid.NewGuid() : s.AccountId, mode == "collection" ? Guid.NewGuid() : a.Collection.PublicId,
            input.GrantId, mode == "identity" ? Guid.NewGuid() : r.Actor, mode == "recipientPin" ? other.Material.RecipientKey : client.Material.RecipientKey, mode == "epoch" ? 2 : 1);
        if (mode == "authorPin") wrap = other.WrapGrant(s.AccountId, a.Collection.PublicId, input.GrantId, r.Actor, client.Material.RecipientKey);
        if (mode == "signature") wrap = wrap with { Signature = ProtectionFixture.Encode(new byte[64]) };
        Assert.True(wrap.IsValid()); await Outcome(s, r, a.Collection, input with { KeyEnvelope = wrap }, "validation_failed");
    }

    [FunctionalTheory]
    [InlineData(false, false)][InlineData(true, false)][InlineData(false, true)][InlineData(true, true)]
    public async Task GivenVisibleForeignCollection_WhenGranting_ThenValidateNativeEvidenceBefore403WithoutRecipientLocks(bool write, bool corrupt)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var g = await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection, write ? CollectionGrantAccess.ReadWrite : CollectionGrantAccess.ReadOnly);
        if (corrupt) await Sql("UPDATE cerberus.collection_grant SET recipient_key_envelope='bad'::bytea WHERE id={0}", g.Id);
        var capture = new Capture(); var result = await Grant(r, a.Collection, Input(s, owner, r, client, a.Collection) with { RecipientAccountId = Guid.NewGuid() }, new ProfileSetup.Factory(fixture, capture));
        Assert.Equal(corrupt ? "persistence_unavailable" : "vault_access_denied", result.Error); Assert.Empty(capture.Locks);
    }

    [FunctionalTheory][InlineData("hidden")][InlineData("revoked")][InlineData("terminal")][InlineData("unselected")]
    public async Task GivenHiddenForeignTarget_WhenGranting_Then404WithoutInspectingRequestedRecipient(string mode)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        if (mode != "hidden") { var g = await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection); if (mode == "revoked") await Sql("UPDATE cerberus.collection_grant SET state=1 WHERE id={0}", g.Id); if (mode == "terminal") await Terminal("grant", g.PublicId); }
        if (mode == "unselected") r = r with { Verifier = await Selected(r, await AssociationSetup.Profile(fixture, r, client)) };
        await Sql("UPDATE cerberus.collection SET envelope='bad'::bytea WHERE id={0}", a.Collection.Id);
        var capture = new Capture(); var result = await Grant(r, a.Collection, Input(s, owner, r, client, a.Collection), new ProfileSetup.Factory(fixture, capture));
        Assert.Equal("not_found", result.Error); Assert.Empty(capture.Locks);
    }

    [FunctionalTheory]
    [InlineData("active", false)][InlineData("trash", false)][InlineData("terminal", false)][InlineData("cycle", false)]
    [InlineData("hiddenCycle", false)][InlineData("missing", false)][InlineData("foreign", false)][InlineData("trash", true)]
    public async Task GivenDirectMembersWithAncestry_WhenGranting_ThenProjectOnlyCompleteActiveDirectRoots(string state, bool recordOnly)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s); var b = await AssociationSetup.Items(fixture, s);
        await Members(s, a);
        await Sql("UPDATE cerberus.record SET folder_id={0} WHERE id={1}", b.Folder.Id, a.Record.Id);
        if (!recordOnly) await Sql("UPDATE cerberus.folder SET parent_folder_id={0} WHERE id={1}", b.Folder.Id, a.Folder.Id);
        if (state is "trash" or "hiddenCycle") await Sql("UPDATE cerberus.folder SET deleted_at=statement_timestamp() WHERE id={0}", b.Folder.Id);
        if (state == "terminal") await Terminal("folder", b.Folder.PublicId);
        if (state is "cycle" or "hiddenCycle") await Sql("UPDATE cerberus.folder SET parent_folder_id=id WHERE id={0}", b.Folder.Id);
        if (state is "missing" or "foreign")
        {
            var foreign = await AssociationSetup.Items(fixture, r); await using var db = fixture.CreateContext(); await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role=replica");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.folder SET parent_folder_id={(state == "missing" ? long.MaxValue : foreign.Folder.Id)} WHERE id={b.Folder.Id}"); await tx.CommitAsync();
        }
        try
        {
            var result = await Grant(s, a.Collection, Input(s, owner, r, client, a.Collection));
            Assert.Equal(state == "cycle" ? "persistence_unavailable" : null, result.Error);
            if (result.Data is { } data) { Assert.Equal(state == "active" ? new[] { a.Record.PublicId } : [], data.RecordIds); Assert.Equal(state == "active" || recordOnly ? new[] { a.Folder.PublicId } : [], data.FolderIds); }
        }
        finally { if (state is "missing" or "foreign") await Sql("UPDATE cerberus.folder SET parent_folder_id=NULL WHERE id={0}", b.Folder.Id); }
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenConcurrentSamePair_WhenGranting_ThenExactlyOneWinnerAndReplayConflicts(bool regrant)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var old = regrant ? await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection) : null;
        if (old is not null) await Sql("UPDATE cerberus.collection_grant SET state=1 WHERE id={0}", old.Id);
        var first = Input(s, owner, r, client, a.Collection, id: old?.PublicId, expected: regrant ? 1 : null);
        var second = Input(s, owner, r, client, a.Collection, "read-write", old?.PublicId, regrant ? 1 : null);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await Task.WhenAll(Grant(s, a.Collection, first, ct: ct.Token), Grant(s, a.Collection, second, ct: ct.Token));
        Assert.Single(result, x => x.Error is null); Assert.Single(result, x => x.Error == "revision_conflict");
        await Outcome(s, r, a.Collection, result[0].Error is null ? first : second, "revision_conflict");
    }

    [FunctionalFact]
    public async Task GivenReciprocalOwners_WhenGrantingConcurrently_ThenBothCommitWithoutForeignKeyDeadlock()
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s); var b = await AssociationSetup.Items(fixture, r);
        var firstReady = Signal(); var secondReady = Signal(); var release = Signal(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = Grant(s, a.Collection, Input(s, owner, r, client, a.Collection), new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), async _ => { firstReady.TrySetResult(); await release.Task.WaitAsync(ct.Token); })), ct.Token);
        var second = Grant(r, b.Collection, Input(r, client, s, owner, b.Collection), new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), async _ => { secondReady.TrySetResult(); await release.Task.WaitAsync(ct.Token); })), ct.Token);
        try { await Task.WhenAll(firstReady.Task, secondReady.Task).WaitAsync(TimeSpan.FromSeconds(10)); } finally { release.TrySetResult(); }
        var results = await Task.WhenAll(first, second); Assert.All(results, x => Assert.Null(x.Error));
        await using var db = fixture.CreateContext(); Assert.Equal(2, await db.CollectionGrants.CountAsync(x => x.CollectionId == a.Collection.Id || x.CollectionId == b.Collection.Id));
    }

    [FunctionalTheory]
    [InlineData("account")][InlineData("recipient")][InlineData("vault_access_session")][InlineData("profile")][InlineData("collection")][InlineData("collection_grant")]
    public async Task GivenNaturalExpiryDuringEachLockWait_WhenGranting_ThenCurrent403AndNoMutation(string table)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var g = await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection); await Sql("UPDATE cerberus.collection_grant SET state=1 WHERE id={0}", g.Id);
        var p = await AssociationSetup.Profile(fixture, s, owner); await Sql("INSERT INTO cerberus.profile_collection(profile_id,collection_id) VALUES({0},{1})", p, a.Collection.Id); s = s with { Verifier = await Selected(s, p) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1.5); await Sql("UPDATE cerberus.vault_access_session SET expires_at={0} WHERE handle_verifier={1}", deadline, s.Verifier);
        var before = await Stable(s, r, a.Collection); var grants = await Grants(a.Collection);
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        var actual = table == "recipient" ? "account" : table; object id = table switch { "account" => s.InternalId, "recipient" => r.InternalId, "vault_access_session" => s.Verifier, "profile" => p, "collection_grant" => g.Id, _ => a.Collection.Id };
        var lockSql = "SELECT FROM cerberus." + actual + " WHERE " + (table == "vault_access_session" ? "handle_verifier={0}" : "id={0}") + " FOR UPDATE";
        await blocker.Database.ExecuteSqlRawAsync(lockSql, id);
        var reached = Signal(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = Grant(s, a.Collection, Input(s, owner, r, client, a.Collection, id: g.PublicId, expected: 1), new ProfileSetup.Factory(fixture, new Before(c => Lock(c, actual), _ => { reached.TrySetResult(); return Task.CompletedTask; })), ct.Token);
        try { await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(pending.IsCompleted); await Past(deadline); } finally { await tx.CommitAsync(); }
        Assert.Equal("vault_access_denied", (await pending).Error); Assert.Equal(before, await Stable(s, r, a.Collection)); Assert.Equal(grants, await Grants(a.Collection));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenExpiryAroundGuardedWrite_WhenGranting_ThenOnlyPreAuthorizationExpiryDenies(bool after)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1.2); await Sql("UPDATE cerberus.vault_access_session SET expires_at={0} WHERE handle_verifier={1}", deadline, s.Verifier);
        DbCommandInterceptor hook = after ? new After(Write, async _ => await Past(deadline)) : new Before(Write, async _ => await Past(deadline));
        var result = await Grant(s, a.Collection, Input(s, owner, r, client, a.Collection), new ProfileSetup.Factory(fixture, hook));
        Assert.Equal(after ? null : "vault_access_denied", result.Error);
    }

    [FunctionalTheory]
    [InlineData("lifecycle", false)][InlineData("lifecycle", true)][InlineData("protection", false)][InlineData("protection", true)]
    [InlineData("membership", false)][InlineData("membership", true)][InlineData("trash", false)][InlineData("trash", true)]
    [InlineData("rotation", false)][InlineData("rotation", true)]
    public async Task GivenRecipientOrOwnerWriterInEitherOrder_WhenGranting_ThenSerializeWithoutDeadlock(string writer, bool grantFirst)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        ProtectionChangeRequest? change = null;
        if (writer is "protection" or "rotation")
        {
            var actor = writer == "protection" ? r : s; var key = writer == "protection" ? client : owner;
            var input = new ProtectionChange(actor.AccountId, 1, 1, writer == "protection" ? "rewrap" : "rotate-content", key.Rewrap(), writer == "protection" ? [] : [new("account", s.AccountId, 1, ProfileSetup.Envelope(2)), new("collection", a.Collection.PublicId, 1, ProfileSetup.Envelope(2)), new("record", a.Record.PublicId, 1, ProfileSetup.Envelope(2)), new("folder", a.Folder.PublicId, 1, ProfileSetup.Envelope(2))]);
            var raw = Bytes(input); var challenge = (await new VaultProtectionStore(fixture).ChallengeAsync(actor.Actor, "change-protection", ProtocolBinary.Encode(SHA256.HashData(raw)), default)).Data!;
            change = new(actor.Actor, actor.Verifier, challenge.ChallengeId, key.Sign(challenge), raw, input);
        }
        var reached = Signal(); var release = Signal(); var waiting = Signal(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<string?> Writer(IDbContextFactory<AppDbContext> f)
        {
            if (writer == "lifecycle")
            {
                await using var db = await f.CreateDbContextAsync(ct.Token); await using var tx = await db.Database.BeginTransactionAsync(ct.Token);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={r.InternalId} FOR UPDATE", ct.Token);
                await db.Accounts.Where(x => x.Id == r.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending), ct.Token); await tx.CommitAsync(ct.Token); return null;
            }
            return writer switch
            {
                "membership" => (await new CollectionMembershipStore(f).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, new(1, ProfileSetup.Envelope(), DateTimeOffset.UnixEpoch, [a.Record.PublicId], [a.Folder.PublicId])), ct.Token)).Error,
                "trash" => (await new CollectionTrashStore(f).TrashAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, 1), ct.Token)).Error,
                _ => (await new VaultProtectionChangeStore(f).ChangeAsync(change!, ct.Token)).Error
            };
        }
        var paused = new ProfileSetup.Factory(fixture, new After(c => Lock(c, "account"), async _ => { reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }));
        var observed = new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), _ => { waiting.TrySetResult(); return Task.CompletedTask; }));
        Task<VaultResult<CollectionGrantDetails>> grant; Task<string?> write;
        if (grantFirst) { grant = Grant(s, a.Collection, Input(s, owner, r, client, a.Collection), paused, ct.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); write = Writer(observed); }
        else { write = Writer(paused); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); grant = Grant(s, a.Collection, Input(s, owner, r, client, a.Collection), observed, ct.Token); }
        try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(grantFirst ? write.IsCompleted : grant.IsCompleted); } finally { release.TrySetResult(); }
        await Task.WhenAll(grant, write);
        Assert.Equal(!grantFirst && writer is "lifecycle" or "trash" ? "not_found" : !grantFirst && writer == "rotation" ? "vault_access_denied" : null, grant.Result.Error);
        Assert.Equal(grantFirst && writer == "rotation" ? "validation_failed" : null, write.Result);
        await using var check = fixture.CreateContext(); var rows = await check.CollectionGrants.Where(x => x.CollectionId == a.Collection.Id).ToArrayAsync();
        if (grant.Result.Error is null) Assert.Equal(grantFirst && writer == "trash" ? CollectionGrantState.Revoked : CollectionGrantState.Active, Assert.Single(rows).State); else Assert.Empty(rows);
    }

    [FunctionalTheory][InlineData("insert")][InlineData("regrant")][InlineData("projection")][InlineData("lateGrant")][InlineData("collection")][InlineData("members")][InlineData("pins")]
    public async Task GivenPostWriteFault_WhenGranting_ThenRollbackEveryChangeAndCleanRetrySucceeds(string stage)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s); await Members(s, a);
        var g = stage == "regrant" ? await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection) : null;
        if (g is not null) await Sql("UPDATE cerberus.collection_grant SET state=1 WHERE id={0}", g.Id);
        var input = Input(s, owner, r, client, a.Collection, id: g?.PublicId, expected: g is null ? null : 1);
        var stable = await Stable(s, r, a.Collection); var grants = await Grants(a.Collection);
        var hook = new After(Write, async c =>
        {
            if (stage is "insert" or "regrant") throw new TimeoutException("controlled post-write fault");
            if (stage == "lateGrant") return;
            await Execute(c, stage switch { "projection" => "DELETE FROM cerberus.collection_grant WHERE public_id=@grant", "collection" => "UPDATE cerberus.collection SET revision=revision+1 WHERE id=@target", "members" => "DELETE FROM cerberus.collection_record WHERE collection_id=@target", _ => "UPDATE cerberus.vault_protection SET revision=revision+1 WHERE account_id=@account" }, new("grant", input.GrantId), new("target", a.Collection.Id), new("account", r.InternalId));
        });
        DbCommandInterceptor actual = stage == "lateGrant" ? new Before(c => c.CommandText.StartsWith("WITH RECURSIVE") && c.Parameters.Cast<DbParameter>().Any(p => p.ParameterName == "authorized" && Equals(p.Value, true)), c => Execute(c, "UPDATE cerberus.collection_grant SET revision=revision+1 WHERE public_id=@grant", new NpgsqlParameter("grant", input.GrantId))) : hook;
        var result = await Grant(s, a.Collection, input, new ProfileSetup.Factory(fixture, actual));
        Assert.Equal("persistence_unavailable", result.Error); Assert.Equal(stable, await Stable(s, r, a.Collection)); Assert.Equal(grants, await Grants(a.Collection));
        Assert.Null((await Grant(s, a.Collection, input)).Error);
    }

    [FunctionalFact]
    public async Task GivenActualGrantForeignKey_WhenRecipientAccountIsHeld_ThenInsertWaitsForItsImplicitKeyShare()
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s); var input = Input(s, owner, r, client, a.Collection);
        await using var blocker = fixture.CreateContext(); await using var held = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={r.InternalId} FOR UPDATE");
        var blockerPid = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        await using var writer = fixture.CreateContext(); await using var transaction = await writer.Database.BeginTransactionAsync();
        var writerPid = await writer.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = writer.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.collection_grant(public_id,collection_id,recipient_account_id,access,state,revision,recipient_key_envelope,concurrency_stamp) VALUES({input.GrantId},{a.Collection.Id},{r.InternalId},0,0,1,{Bytes(input.KeyEnvelope)},{Guid.NewGuid()})", ct.Token);
        try
        {
            await using var observer = fixture.CreateContext(); bool waiting;
            do { waiting = await observer.Database.SqlQuery<bool>($"SELECT {blockerPid}=ANY(pg_blocking_pids({writerPid})) AS \"Value\"").SingleAsync(ct.Token); if (!waiting) await Task.Delay(20, ct.Token); } while (!waiting);
            Assert.False(pending.IsCompleted);
        }
        finally { await held.CommitAsync(); }
        Assert.Equal(1, await pending); await transaction.RollbackAsync();
    }

    [FunctionalFact]
    public async Task GivenLowerRecipientAccountHeld_WhenOwnerHasHigherPublicId_ThenNeverHoldHigherAccountOrCollectionEarly()
    {
        using var keyA = new ProtectionFixture(); using var keyB = new ProtectionFixture(); var stateA = await ProfileSetup.Create(fixture, keyA); var stateB = await ProfileSetup.Create(fixture, keyB);
        var s = stateA.AccountId.CompareTo(stateB.AccountId) > 0 ? stateA : stateB; var r = s == stateA ? stateB : stateA;
        var owner = s == stateA ? keyA : keyB; var client = s == stateA ? keyB : keyA; var a = await AssociationSetup.Items(fixture, s);
        await using var blocker = fixture.CreateContext(); await using var held = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={r.InternalId} FOR UPDATE");
        var blockerPid = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(); var grantPid = 0;
        var reached = Signal(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = Grant(s, a.Collection, Input(s, owner, r, client, a.Collection), new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), c => { grantPid = ((NpgsqlConnection)c.Connection!).ProcessID; reached.TrySetResult(); return Task.CompletedTask; })), ct.Token);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var observer = fixture.CreateContext(); await using var tx = await observer.Database.BeginTransactionAsync();
            while (!await observer.Database.SqlQuery<bool>($"SELECT {blockerPid}=ANY(pg_blocking_pids({grantPid})) AS \"Value\"").SingleAsync(ct.Token)) await Task.Delay(20, ct.Token);
            await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE NOWAIT", ct.Token);
            await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE id={a.Collection.Id} FOR UPDATE NOWAIT", ct.Token);
            Assert.False(pending.IsCompleted); await tx.RollbackAsync();
        }
        finally { await held.CommitAsync(); }
        Assert.Null((await pending).Error);
    }

    [FunctionalFact]
    public async Task GivenUnrelatedInventory_WhenGranting_ThenOneBoundedTargetOrderedLocksAndOneGrantMutation()
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s); await Members(s, a);
        var p = await AssociationSetup.Profile(fixture, s, owner); await Sql("INSERT INTO cerberus.profile_collection(profile_id,collection_id) VALUES({0},{1})", p, a.Collection.Id); s = s with { Verifier = await Selected(s, p) };
        await using (var db = fixture.CreateContext()) { db.Collections.AddRange(Enumerable.Range(0, 100).Select(i => new VaultCollection { PublicId = Guid.NewGuid(), AccountId = i < 50 ? s.InternalId : r.InternalId, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch })); await db.SaveChangesAsync(); }
        var capture = new Capture(); Assert.Null((await Grant(s, a.Collection, Input(s, owner, r, client, a.Collection), new ProfileSetup.Factory(fixture, capture))).Error);
        Assert.Equal(5, capture.Locks.Count); Assert.Contains("FROM cerberus.account", capture.Locks[0]); Assert.Contains("ORDER BY public_id FOR UPDATE", capture.Locks[0]);
        Assert.Equal(new[] { s.AccountId, r.AccountId }.Order(), Assert.Single(capture.Values[0].OfType<Guid[]>()).Order());
        Assert.Contains("vault_access_session", capture.Locks[1]); Assert.Contains("profile", capture.Locks[2]); Assert.Contains("FOR NO KEY UPDATE", capture.Locks[2]); Assert.Contains("collection WHERE", capture.Locks[3]); Assert.Contains("collection_grant", capture.Locks[4]);
        Assert.Equal(1, capture.Writes);
        await using var check = fixture.CreateContext(); await check.Database.OpenConnectionAsync(); await using var explain = check.Database.GetDbConnection().CreateCommand(); explain.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + capture.Snapshot; foreach (var parameter in capture.Parameters) explain.Parameters.Add(parameter.Clone());
        using var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
        static IEnumerable<JsonElement> Nodes(JsonElement n) { yield return n; if (n.TryGetProperty("Plans", out var children)) foreach (var child in children.EnumerateArray()) foreach (var descendant in Nodes(child)) yield return descendant; }
        var target = Assert.Single(Nodes(plan.RootElement[0].GetProperty("Plan")), x => x.TryGetProperty("Subplan Name", out var name) && name.GetString() == "CTE target"); Assert.Equal(1d, target.GetProperty("Actual Rows").GetDouble());
    }

    [FunctionalTheory]
    [InlineData("recipientState", "not_found")][InlineData("recipientTerminal", "not_found")][InlineData("recipientKey", "validation_failed")]
    [InlineData("ownerPolicy", "vault_access_denied")][InlineData("selection", "not_found")][InlineData("target", "not_found")][InlineData("grant", "revision_conflict")]
    public async Task GivenStateLossWhileWaiting_WhenGranting_ThenRecheckBeforeMutation(string state, string error)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); using var other = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var g = await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection); await Sql("UPDATE cerberus.collection_grant SET state=1 WHERE id={0}", g.Id);
        var p = await AssociationSetup.Profile(fixture, s, owner); await Sql("INSERT INTO cerberus.profile_collection(profile_id,collection_id) VALUES({0},{1})", p, a.Collection.Id); s = s with { Verifier = await Selected(s, p) };
        var table = state == "grant" ? "collection_grant" : state == "target" ? "collection" : state == "selection" ? "profile" : "account";
        var id = state == "grant" ? g.Id : state == "target" ? a.Collection.Id : state == "selection" ? p : state.StartsWith("recipient") ? r.InternalId : s.InternalId;
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        var sql = "SELECT FROM cerberus." + table + " WHERE id={0} FOR UPDATE"; await blocker.Database.ExecuteSqlRawAsync(sql, id);
        var reached = Signal(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = Grant(s, a.Collection, Input(s, owner, r, client, a.Collection, id: g.PublicId, expected: 1), new ProfileSetup.Factory(fixture, new Before(c => Lock(c, table), _ => { reached.TrySetResult(); return Task.CompletedTask; })), ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(pending.IsCompleted);
        if (state == "recipientState") await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET state={(int)AccountState.ClosurePending} WHERE id={r.InternalId}");
        if (state == "recipientTerminal") { blocker.TerminalErasures.Add(new() { ResourceKind = "account", ResourceId = r.AccountId, DeletedAt = DateTimeOffset.UtcNow }); await blocker.SaveChangesAsync(); }
        if (state == "recipientKey") await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.vault_protection SET material={Bytes(other.Material)} WHERE account_id={r.InternalId}");
        if (state == "ownerPolicy") await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET policy_revision=policy_revision+1 WHERE id={s.InternalId}");
        if (state == "selection") await blocker.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cerberus.profile_collection WHERE profile_id={p} AND collection_id={a.Collection.Id}");
        if (state == "target") await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.collection SET deleted_at=statement_timestamp() WHERE id={a.Collection.Id}");
        if (state == "grant") await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.collection_grant SET revision=revision+1 WHERE id={g.Id}");
        await tx.CommitAsync(); Assert.Equal(error, (await pending).Error);
        await using var check = fixture.CreateContext(); Assert.Equal(CollectionGrantState.Revoked, (await check.CollectionGrants.SingleAsync(x => x.Id == g.Id)).State);
    }

    [FunctionalTheory][InlineData("actor", "authentication_required")][InlineData("handle", "vault_access_required")][InlineData("target", "validation_failed")][InlineData("input", "validation_failed")]
    public async Task GivenInvalidTrustedBoundary_WhenGranting_ThenRejectBeforePersistence(string state, string error)
    {
        using var owner = new ProtectionFixture(); var id = Guid.NewGuid(); var input = new CollectionGrantInput(id, Guid.NewGuid(), null, "read-only", owner.WrapGrant(Guid.NewGuid(), Guid.NewGuid(), id, Guid.NewGuid(), owner.Material.RecipientKey));
        var request = new CollectionGrantRequest(state == "actor" ? Guid.Empty : Guid.NewGuid(), state == "handle" ? "" : "v", state == "target" ? Guid.Empty : Guid.NewGuid(), state == "input" ? null! : input);
        Assert.Equal(error, (await new CollectionGrantStore(new ThrowingFactory(new Exception("must not persist"))).GrantAsync(request, default)).Error);
    }

    [FunctionalTheory][InlineData("timeout")][InlineData("cancel")][InlineData("db")][InlineData("update")][InlineData("json")][InlineData("format")][InlineData("wrapped")][InlineData("unique")]
    public async Task GivenInternalFailure_WhenGranting_ThenSafeErrorOnly(string kind)
    {
        using var owner = new ProtectionFixture(); var id = Guid.NewGuid(); var input = new CollectionGrantInput(id, Guid.NewGuid(), null, "read-only", owner.WrapGrant(Guid.NewGuid(), Guid.NewGuid(), id, Guid.NewGuid(), owner.Material.RecipientKey));
        Exception exception = kind switch { "cancel" => new OperationCanceledException("private"), "db" => new NpgsqlException("private"), "update" => new DbUpdateException("private"), "json" => new JsonException("private"), "format" => new FormatException("private"), "wrapped" => new InvalidOperationException("private", new TimeoutException("private")), "unique" => new PostgresException("private", "ERROR", "ERROR", "23505"), _ => new TimeoutException("private") };
        Assert.Equal(kind == "unique" ? "revision_conflict" : "persistence_unavailable", (await new CollectionGrantStore(new ThrowingFactory(exception)).GrantAsync(new(Guid.NewGuid(), "v", Guid.NewGuid(), input), default)).Error);
    }

    [FunctionalFact]
    public async Task GivenCallerCancellationDuringRecipientLockWait_WhenGranting_ThenPropagateAndRollback()
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync(); await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={r.InternalId} FOR UPDATE");
        var reached = Signal(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = Grant(s, a.Collection, Input(s, owner, r, client, a.Collection), new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), _ => { reached.TrySetResult(); return Task.CompletedTask; })), ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); await tx.RollbackAsync(); Assert.Equal("[]", await Grants(a.Collection));
    }

    [FunctionalFact]
    public async Task GivenLastSafeRevokedRevision_WhenGranting_ThenAdvanceToMaximumExactlyOnce()
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var g = await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection); await Sql("UPDATE cerberus.collection_grant SET state=1,revision={0} WHERE id={1}", ProtocolBinary.MaxInteger - 1, g.Id);
        var result = await Grant(s, a.Collection, Input(s, owner, r, client, a.Collection, id: g.PublicId, expected: ProtocolBinary.MaxInteger - 1));
        Assert.Null(result.Error); Assert.Equal(ProtocolBinary.MaxInteger, result.Data!.Revision);
    }

    [FunctionalFact]
    public async Task GivenMultipleTypedDirectMembers_WhenGranting_ThenSortExactlyAndExcludeUnlinkedDescendants()
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s); var b = await AssociationSetup.Items(fixture, s, Guid.NewGuid()); var child = await AssociationSetup.Items(fixture, s);
        await Members(s, a); await Sql("INSERT INTO cerberus.collection_folder(account_id,collection_id,folder_id) VALUES({0},{1},{2})", s.InternalId, a.Collection.Id, b.Folder.Id); await Sql("INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES({0},{1},{2})", s.InternalId, a.Collection.Id, b.Record.Id);
        await Sql("UPDATE cerberus.folder SET parent_folder_id={0} WHERE id={1}", a.Folder.Id, child.Folder.Id); await Sql("UPDATE cerberus.record SET folder_id={0} WHERE id={1}", child.Folder.Id, child.Record.Id);
        var result = await Grant(s, a.Collection, Input(s, owner, r, client, a.Collection)); Assert.Null(result.Error);
        Assert.Equal(new[] { a.Record.PublicId, b.Record.PublicId }.Order(), result.Data!.RecordIds); Assert.Equal(new[] { a.Folder.PublicId, b.Folder.PublicId }.Order(), result.Data.FolderIds);
    }

    [FunctionalTheory]
    [InlineData("policy", "vault_access_denied")][InlineData("recipient", "not_found")][InlineData("selection", "not_found")]
    [InlineData("collection", "revision_conflict")][InlineData("grant", "revision_conflict")][InlineData("terminal", "revision_conflict")]
    public async Task GivenLateChangeAtGuardedStatement_WhenGranting_ThenFailClosedAndRollback(string state, string error)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var p = await AssociationSetup.Profile(fixture, s, owner); await Sql("INSERT INTO cerberus.profile_collection(profile_id,collection_id) VALUES({0},{1})", p, a.Collection.Id); s = s with { Verifier = await Selected(s, p) };
        var input = Input(s, owner, r, client, a.Collection); var stable = await Stable(s, r, a.Collection);
        var hook = new Before(Write, c => Execute(c, state switch
        {
            "policy" => "UPDATE cerberus.account SET policy_revision=policy_revision+1 WHERE id=@owner",
            "recipient" => "UPDATE cerberus.account SET state=@closing WHERE id=@recipient",
            "selection" => "DELETE FROM cerberus.profile_collection WHERE profile_id=@profile AND collection_id=@target",
            "collection" => "UPDATE cerberus.collection SET revision=revision+1 WHERE id=@target",
            "grant" => "INSERT INTO cerberus.collection_grant(public_id,collection_id,recipient_account_id,access,state,revision,recipient_key_envelope,concurrency_stamp) VALUES(@grant,@target,@recipient,0,0,1,@envelope,@grant)",
            _ => "INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at) VALUES('grant',@grant,statement_timestamp())"
        }, new("owner", s.InternalId), new("recipient", r.InternalId), new("closing", (object)(int)AccountState.ClosurePending), new("profile", p), new("target", a.Collection.Id), new("grant", input.GrantId), new("envelope", Bytes(input.KeyEnvelope))));
        Assert.Equal(error, (await Grant(s, a.Collection, input, new ProfileSetup.Factory(fixture, hook))).Error);
        Assert.Equal(stable, await Stable(s, r, a.Collection)); Assert.Equal("[]", await Grants(a.Collection));
        Assert.Null((await Grant(s, a.Collection, input)).Error);
    }

    private sealed class ThrowingFactory(Exception failure) : IDbContextFactory<AppDbContext>
    { public AppDbContext CreateDbContext() => throw failure; public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromException<AppDbContext>(failure); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool Lock(DbCommand c, string table) => c.CommandText.Contains("FROM cerberus." + table + " ") && (c.CommandText.Contains("FOR UPDATE") || c.CommandText.Contains("FOR NO KEY UPDATE"));
    private static bool Write(DbCommand c) => c.CommandText.Contains("INSERT INTO cerberus.collection_grant(") || c.CommandText.Contains("UPDATE cerberus.collection_grant AS g");
    private static async Task Past(DateTimeOffset deadline) { var remaining = deadline - DateTimeOffset.UtcNow; if (remaining > TimeSpan.Zero) await Task.Delay(remaining + TimeSpan.FromMilliseconds(100)); }
    private static async Task Execute(DbCommand c, string sql, params NpgsqlParameter[] parameters)
    { await using var command = c.Connection!.CreateCommand(); command.Transaction = c.Transaction; command.CommandText = sql; foreach (var p in parameters) command.Parameters.Add(p); await command.ExecuteNonQueryAsync(); }
    private sealed class Before(Func<DbCommand, bool> match, Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        private bool used;
        private async Task Check(DbCommand c) { if (!used && match(c)) { used = true; await action(c); } }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default) { await Check(c); return r; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { await Check(c); return r; }
    }
    private sealed class After(Func<DbCommand, bool> match, Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        private bool used;
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken ct = default) { if (!used && match(c)) { used = true; await action(c); } return r; }
    }

    private async Task Outcome(ProfileSetup.State s, ProfileSetup.State r, VaultCollection c, CollectionGrantInput input, string? error)
    {
        var before = await Stable(s, r, c); var grants = await Grants(c);
        var result = await Grant(s, c, input); Assert.Equal(error, result.Error);
        Assert.Equal(before, await Stable(s, r, c));
        if (error is not null) { Assert.Null(result.Data); Assert.Equal(grants, await Grants(c)); }
    }
    private async Task<string> Grants(VaultCollection c) { await using var db = fixture.CreateContext(); return await db.Database.SqlQuery<string>($"SELECT COALESCE(jsonb_agg(to_jsonb(g) ORDER BY id),'[]'::jsonb)::text AS \"Value\" FROM cerberus.collection_grant g WHERE collection_id={c.Id}").SingleAsync(); }
    private async Task Terminal(string kind, Guid id) { await using var db = fixture.CreateContext(); db.TerminalErasures.Add(new() { ResourceKind = kind, ResourceId = id, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
    private async Task<string> Selected(ProfileSetup.State s, long p)
    {
        var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); await using var db = fixture.CreateContext();
        db.VaultAccessSessions.Add(new() { AccountId = s.InternalId, ProfileId = p, HandleVerifier = verifier, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 }); await db.SaveChangesAsync(); return verifier;
    }
    private sealed class Capture : DbCommandInterceptor
    {
        public List<string> Locks { get; } = [];
        public List<object[]> Values { get; } = []; public int Writes { get; private set; }
        public string Snapshot { get; private set; } = ""; public NpgsqlParameter[] Parameters { get; private set; } = [];
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default)
        { if (c.CommandText.Contains("FOR UPDATE") || c.CommandText.Contains("FOR NO KEY UPDATE")) { Locks.Add(c.CommandText); Values.Add(c.Parameters.Cast<DbParameter>().Select(x => x.Value!).ToArray()); } if (Write(c)) Writes++; return ValueTask.FromResult(r); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
        { if (Snapshot.Length == 0 && c.CommandText.StartsWith("WITH RECURSIVE")) { Snapshot = c.CommandText; Parameters = c.Parameters.Cast<NpgsqlParameter>().Select(x => x.Clone()).ToArray(); } return ValueTask.FromResult(r); }
    }

    private static CollectionGrantInput Input(ProfileSetup.State s, ProtectionFixture owner, ProfileSetup.State r,
        ProtectionFixture client, VaultCollection c, string access = "read-only", Guid? id = null, long? expected = null)
    {
        var grant = id ?? Guid.NewGuid();
        return new(grant, r.AccountId, expected, access, owner.WrapGrant(s.AccountId, c.PublicId, grant, r.Actor, client.Material.RecipientKey, c.KeyEpoch, (expected ?? 0) + 1));
    }
    private Task<VaultResult<CollectionGrantDetails>> Grant(ProfileSetup.State s, VaultCollection c, CollectionGrantInput input, IDbContextFactory<AppDbContext>? factory = null, CancellationToken ct = default)
        => new CollectionGrantStore(factory ?? fixture).GrantAsync(new(s.Actor, s.Verifier, c.PublicId, input), ct);
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ProtectionFixture.Json);
    private async Task Sql(string sql, params object[] values) { await using var db = fixture.CreateContext(); await db.Database.ExecuteSqlRawAsync(sql, values); }
    private async Task Members(ProfileSetup.State s, AssociationSetup.ResourceSet a)
    {
        await using var db = fixture.CreateContext();
        db.CollectionFolders.Add(new() { AccountId = s.InternalId, CollectionId = a.Collection.Id, FolderId = a.Folder.Id });
        db.CollectionRecords.Add(new() { AccountId = s.InternalId, CollectionId = a.Collection.Id, RecordId = a.Record.Id });
        await db.SaveChangesAsync();
    }
    private async Task<string> Stable(ProfileSetup.State s, ProfileSetup.State r, VaultCollection c)
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
              'accounts',(SELECT jsonb_agg(to_jsonb(a) ORDER BY id) FROM cerberus.account a WHERE id IN ({s.InternalId},{r.InternalId})),
              'protection',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM cerberus.vault_protection p WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'sessions',(SELECT jsonb_agg(to_jsonb(v) ORDER BY id) FROM cerberus.vault_access_session v WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'profiles',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM cerberus.profile p WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'collections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY id) FROM cerberus.collection c WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.record r WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'folders',(SELECT jsonb_agg(to_jsonb(f) ORDER BY id) FROM cerberus.folder f WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'pc',(SELECT jsonb_agg(to_jsonb(pc) ORDER BY profile_id) FROM cerberus.profile_collection pc WHERE collection_id={c.Id}),
              'cf',(SELECT jsonb_agg(to_jsonb(cf) ORDER BY folder_id) FROM cerberus.collection_folder cf WHERE collection_id={c.Id}),
              'cr',(SELECT jsonb_agg(to_jsonb(cr) ORDER BY record_id) FROM cerberus.collection_record cr WHERE collection_id={c.Id}))::text AS "Value"
            """).SingleAsync();
    }
}
