using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class CollectionCreateStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("empty")][InlineData("profiles")][InlineData("mixed")][InlineData("overlap")][InlineData("typedAlias")]
    public async Task GivenOwnedInitialLinks_WhenCreating_ThenCommitExactTypedLinksAndOnlyDistinctDirectStructuralChanges(string kind)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); using var other = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, owner); var root = await Folder(s); var child = await Folder(s, root.Id); var record = await Record(s, child.Id);
        var p = await Profile(s, owner, scoped); var q = await Profile(s, owner, other); var id = Guid.NewGuid();
        if (kind == "typedAlias")
        {
            id = p.PublicId;
            await using var db = fixture.CreateContext(); await db.Folders.Where(x => x.Id == child.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.PublicId, id));
            await db.Records.Where(x => x.Id == record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.PublicId, id)); child = await FolderRow(child.Id); record = await RecordRow(record.Id);
        }
        var input = Input() with { CollectionId = id, ProfileIds = kind == "empty" ? [] : kind == "typedAlias" ? [id] : [p.PublicId, q.PublicId],
            FolderIds = kind is "mixed" or "overlap" or "typedAlias" ? [child.PublicId] : [],
            RecordIds = kind is "overlap" or "typedAlias" ? [record.PublicId] : [] };
        var before = await Snapshot(s); var result = await Create(s, input); await Success(s, input, before, result);
        await using var check = fixture.CreateContext();
        Assert.Equal(Bytes(root), Bytes(await FolderRow(root.Id))); // An ancestor is not a directly changed member.
        Assert.Empty(await check.CollectionGrants.Where(x => x.CollectionId == check.Collections.Where(c => c.PublicId == id).Select(c => c.Id).Single()).ToArrayAsync());
    }

    [FunctionalTheory]
    [InlineData("PF")][InlineData("PFDescendant")][InlineData("PR")][InlineData("CF")][InlineData("CFDescendant")][InlineData("CR")]
    [InlineData("mixed")][InlineData("overlap")][InlineData("brokenCollection")][InlineData("unusedCollectionCounters")][InlineData("brokenMemberCipher")]
    public async Task GivenAlreadyVisibleOwnedSelection_WhenCreating_ThenRetainScopeAndEveryUnchangedOpaqueByte(string kind)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, owner); var root = await Folder(s); var child = await Folder(s, root.Id); var r = await Record(s, child.Id); var c = await Collection(s);
        var p = await Profile(s, owner, scoped); s = s with { Verifier = await Selected(s, p.Id) };
        await Routes(s, p.Id, c.Id, kind is "PF" or "PFDescendant" or "mixed" ? [root.Id] : [], kind is "PR" or "mixed" ? [r.Id] : [],
            kind is "CF" or "CFDescendant" or "overlap" or "brokenCollection" or "unusedCollectionCounters" or "brokenMemberCipher" ? [root.Id] : [], kind is "CR" or "overlap" ? [r.Id] : []);
        await using (var db = fixture.CreateContext())
        {
            if (kind == "brokenCollection") await db.Collections.Where(x => x.Id == c.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Envelope, new byte[] { 255 }));
            if (kind == "unusedCollectionCounters") await db.Collections.Where(x => x.Id == c.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revision, 0).SetProperty(v => v.ServerSequence, 0));
            if (kind == "brokenMemberCipher") await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Envelope, new byte[] { 255 }));
        }
        var input = Input([p.PublicId], kind is "PR" or "CR" ? [] : [kind is "PFDescendant" or "CFDescendant" ? child.PublicId : root.PublicId],
            kind is "PR" or "CR" or "mixed" or "overlap" ? [r.PublicId] : []);
        var before = await Snapshot(s); await Success(s, input, before, await Create(s, input));
        await using var check = fixture.CreateContext(); Assert.Equal(p.Id, (await check.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == s.Verifier)).ProfileId);
    }

    [FunctionalTheory]
    [InlineData("empty", "vault_access_denied")][InlineData("other", "not_found")][InlineData("multiple", "not_found")]
    [InlineData("privateFolder", "not_found")][InlineData("privateRecord", "not_found")][InlineData("PRFolder", "not_found")]
    [InlineData("CRFolder", "not_found")][InlineData("childParent", "not_found")][InlineData("childSibling", "not_found")]
    [InlineData("hiddenCycle", "not_found")][InlineData("privateCorrupt", "not_found")]
    public async Task GivenSelectedScopeExpansion_WhenCreating_ThenNonrevealingDenialAndNoMutation(string kind, string error)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); using var other = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, owner); var root = await Folder(s); var child = await Folder(s, root.Id); var sibling = await Folder(s, root.Id); var r = await Record(s, child.Id);
        var p = await Profile(s, owner, scoped); var q = await Profile(s, owner, other); var c = await Collection(s); s = s with { Verifier = await Selected(s, p.Id) };
        await Routes(s, p.Id, c.Id, kind is "childParent" or "childSibling" ? [child.Id] : [], kind == "PRFolder" ? [r.Id] : [], [], kind == "CRFolder" ? [r.Id] : []);
        await using (var db = fixture.CreateContext())
        {
            if (kind == "hiddenCycle") await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ParentFolderId, child.Id));
            if (kind == "privateCorrupt") await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revision, 0));
        }
        var input = Input(kind == "empty" ? [] : kind == "other" ? [q.PublicId] : kind == "multiple" ? [p.PublicId, q.PublicId] : [p.PublicId],
            kind is "empty" or "other" or "multiple" or "privateRecord" ? [] : [kind == "childSibling" ? sibling.PublicId : root.PublicId], kind == "privateRecord" ? [r.PublicId] : []);
        var before = await Snapshot(s); var result = await Create(s, input); Assert.Equal(error, result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("missing", "not_found")][InlineData("closing", "not_found")][InlineData("terminal", "not_found")]
    [InlineData("access", "vault_access_denied")][InlineData("revoked", "vault_access_denied")][InlineData("expired", "vault_access_denied")]
    [InlineData("future", "vault_access_denied")][InlineData("policy", "vault_access_denied")][InlineData("generation", "vault_access_denied")]
    [InlineData("zeroPolicy", "vault_access_denied")][InlineData("zeroGeneration", "vault_access_denied")]
    [InlineData("nullExpiry", "vault_access_denied")][InlineData("disabledExpiry", "vault_access_denied")][InlineData("noExpiry", null)]
    [InlineData("selection", "vault_access_denied")]
    public async Task GivenInvalidCurrentAuthority_WhenCreating_ThenFailClosed(string kind, string? error)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner);
        await using (var db = fixture.CreateContext())
        {
            var a = await db.Accounts.SingleAsync(x => x.Id == s.InternalId); var v = await db.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == s.Verifier);
            if (kind == "closing") a.State = AccountState.ClosurePending; if (kind == "terminal") db.TerminalErasures.Add(new() { ResourceKind = "account", ResourceId = s.AccountId, DeletedAt = DateTimeOffset.UtcNow });
            if (kind == "revoked") v.Revoked = true; if (kind == "expired") v.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); if (kind == "future") v.IssuedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            if (kind == "policy") v.PolicyRevision++; if (kind == "generation") v.RevocationGeneration++; if (kind == "zeroPolicy") v.PolicyRevision = 0; if (kind == "zeroGeneration") v.RevocationGeneration = 0;
            if (kind is "noExpiry" or "disabledExpiry") a.RenewalEnabled = false; if (kind is "noExpiry" or "nullExpiry") v.ExpiresAt = null; if (kind == "selection") v.ProfileId = long.MaxValue; await db.SaveChangesAsync();
        }
        var before = await Snapshot(s); var input = Input(); var result = await Store().CreateAsync(new(kind == "missing" ? Guid.NewGuid() : s.Actor, kind == "access" ? new string('a', 64) : s.Verifier, input), default);
        Assert.Equal(error, result.Error); if (error is null) await Success(s, input, before, result); else { Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s)); }
    }

    [FunctionalTheory][InlineData("profile")][InlineData("folder")][InlineData("record")]
    [InlineData("profile", "trash")][InlineData("folder", "trash")][InlineData("record", "trash")]
    [InlineData("profile", "terminal")][InlineData("folder", "terminal")][InlineData("record", "terminal")]
    [InlineData("profile", "foreign")][InlineData("folder", "foreign")][InlineData("record", "foreign")]
    public async Task GivenMissingHiddenOrTerminalRelationship_WhenCreating_Then404AndBothGraphsUnchanged(string kind, string state = "missing")
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var foreign = await ProfileSetup.Create(fixture, owner);
        var targetOwner = state == "foreign" ? foreign : s; var p = await Profile(targetOwner, owner, scoped); var f = await Folder(targetOwner); var r = await Record(targetOwner, f.Id);
        var id = kind == "profile" ? p.PublicId : kind == "folder" ? f.PublicId : r.PublicId;
        await using (var db = fixture.CreateContext())
        {
            if (state == "terminal") { db.TerminalErasures.Add(new() { ResourceKind = kind, ResourceId = id, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
            if (state == "trash") { var sql = "UPDATE cerberus." + kind + " SET deleted_at=statement_timestamp() WHERE public_id={0}"; await db.Database.ExecuteSqlRawAsync(sql, id); }
        }
        var input = Input(kind == "profile" ? [state == "missing" ? Guid.NewGuid() : id] : [], kind == "folder" ? [state == "missing" ? Guid.NewGuid() : id] : [], kind == "record" ? [state == "missing" ? Guid.NewGuid() : id] : []);
        var before = await Snapshot(s); var other = await Snapshot(foreign); var result = await Create(s, input); Assert.Equal("not_found", result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s)); Assert.Equal(other, await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task GivenActuallyReadableForeignNativeMember_WhenCreatingOwnedCollection_Then404WithoutOwnershipTransfer(bool write, bool folder)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var recipient = await ProfileSetup.Create(fixture, client);
        var items = await AssociationSetup.Items(fixture, s); await Members(s, items.Collection.Id, [items.Folder.Id], [items.Record.Id]);
        await AssociationSetup.Grant(fixture, s, owner, recipient, client, items.Collection, write ? CollectionGrantAccess.ReadWrite : CollectionGrantAccess.ReadOnly);
        Assert.Null(folder ? (await new FolderReadStore(fixture).ReadAsync(new(recipient.Actor, recipient.Verifier, items.Folder.PublicId), default)).Error : (await new RecordReadStore(fixture).ReadAsync(new(recipient.Actor, recipient.Verifier, items.Record.PublicId), default)).Error);
        var before = await Snapshot(s); var other = await Snapshot(recipient); var result = await Create(recipient, Input(folders: folder ? [items.Folder.PublicId] : [], records: folder ? [] : [items.Record.PublicId]));
        Assert.Equal("not_found", result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s)); Assert.Equal(other, await Snapshot(recipient));
    }

    [FunctionalTheory][InlineData("cycle", "persistence_unavailable")][InlineData("trash", "not_found")][InlineData("terminal", "not_found")][InlineData("hiddenCycle", "not_found")][InlineData("unrelated", null)]
    public async Task GivenCompleteOwnedAncestry_WhenCreating_ThenRejectRelevantCorruptionAndIgnoreUnrelatedCycles(string kind, string? error)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var root = await Folder(s); var child = await Folder(s, root.Id); var target = kind == "unrelated" ? await Folder(s) : child;
        await using (var db = fixture.CreateContext())
        {
            if (kind is "cycle" or "hiddenCycle" or "unrelated") await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ParentFolderId, child.Id));
            if (kind is "trash" or "hiddenCycle") await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow));
            if (kind == "terminal") { db.TerminalErasures.Add(new() { ResourceKind = "folder", ResourceId = root.PublicId, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        }
        var input = Input(folders: [target.PublicId]); var before = await Snapshot(s); var result = await Create(s, input); Assert.Equal(error, result.Error);
        if (error is null) await Success(s, input, before, result); else { Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s)); }
    }

    [FunctionalTheory][InlineData("profile")][InlineData("folder")][InlineData("record")]
    [InlineData("profile", "sequence")][InlineData("folder", "sequence")][InlineData("record", "sequence")]
    [InlineData("profile", "unsafe")][InlineData("folder", "unsafe")][InlineData("record", "unsafe")]
    [InlineData("profile", "time")][InlineData("folder", "time")][InlineData("record", "time")]
    [InlineData("profile", "purge")][InlineData("folder", "purge")][InlineData("record", "purge")]
    [InlineData("profile", "max")][InlineData("folder", "max")][InlineData("record", "max")]
    public async Task GivenInvalidDirectlyChangedStructuralMetadata_WhenCreating_ThenNoPartialGraph(string kind, string field = "revision")
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner);
        var p = await Profile(s, owner, scoped); var f = await Folder(s); var r = await Record(s, f.Id); var id = kind == "profile" ? p.Id : kind == "folder" ? f.Id : r.Id;
        await using (var db = fixture.CreateContext())
        {
            var expression = field switch { "sequence" => "server_sequence=0", "unsafe" => "server_sequence=9007199254740992", "time" => "edited_at='0001-01-01 00:00:00+00'", "purge" => "purge_at=statement_timestamp()", "max" => "revision=9007199254740991", _ => "revision=0" };
            var sql = "UPDATE cerberus." + kind + " SET " + expression + " WHERE id={0}"; await db.Database.ExecuteSqlRawAsync(sql, id);
        }
        var before = await Snapshot(s); var result = await Create(s, Input([p.PublicId], [f.PublicId], [r.PublicId]));
        Assert.Equal(field == "max" ? "revision_conflict" : "persistence_unavailable", result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("retained")][InlineData("trash")][InlineData("foreign")][InlineData("terminal")]
    public async Task GivenReservedCollectionId_WhenCreating_Then409AndPreserveWinner(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var foreign = await ProfileSetup.Create(fixture, owner); var input = Input();
        await using (var db = fixture.CreateContext())
        {
            if (kind == "terminal") db.TerminalErasures.Add(new() { ResourceId = input.CollectionId, ResourceKind = "collection", DeletedAt = DateTimeOffset.UtcNow });
            else db.Collections.Add(new() { PublicId = input.CollectionId, AccountId = kind == "foreign" ? foreign.InternalId : s.InternalId, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch, DeletedAt = kind == "trash" ? DateTimeOffset.UtcNow : null });
            await db.SaveChangesAsync();
        }
        var before = await Snapshot(s); var other = await Snapshot(foreign); var result = await Create(s, input);
        Assert.Equal("revision_conflict", result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s)); Assert.Equal(other, await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData("account")][InlineData("profile")][InlineData("folder")][InlineData("record")][InlineData("grant")]
    public async Task GivenOtherKindTerminalId_WhenCreatingCollection_ThenRetainBothTypedIdentities(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var input = Input();
        await using (var db = fixture.CreateContext()) { db.TerminalErasures.Add(new() { ResourceId = input.CollectionId, ResourceKind = kind, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        var before = await Snapshot(s); await Success(s, input, before, await Create(s, input));
        await using var check = fixture.CreateContext(); Assert.True(await check.TerminalErasures.AnyAsync(x => x.ResourceId == input.CollectionId && x.ResourceKind == kind));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenConcurrentOrReplayedCollectionCreation_WhenCreating_ThenExactlyOneWinnerAndNoDuplicateBumps(bool differentOwners)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var other = differentOwners ? await ProfileSetup.Create(fixture, owner) : s; var input = Input();
        var results = await Task.WhenAll(Create(s, input), Create(other, input)); Assert.Single(results, x => x.Error is null); Assert.Equal("revision_conflict", Assert.Single(results, x => x.Error is not null).Error);
        var before = await Snapshot(s); var otherBefore = await Snapshot(other); Assert.Equal("revision_conflict", (await Create(s, input)).Error); Assert.Equal(before, await Snapshot(s)); Assert.Equal(otherBefore, await Snapshot(other));
        await using var db = fixture.CreateContext(); Assert.Single(await db.Collections.Where(x => x.PublicId == input.CollectionId).ToArrayAsync());
    }

    [FunctionalTheory][InlineData("insert")][InlineData("PC")][InlineData("CF")][InlineData("CR")][InlineData("profile")][InlineData("folder")][InlineData("record")]
    public async Task GivenFailureAfterActualMutation_WhenCreating_ThenRollBackEverythingAndAllowSameIdRetry(string stage)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var p = await Profile(s, owner, scoped); var f = await Folder(s); var r = await Record(s, f.Id);
        var input = Input([p.PublicId], [f.PublicId], [r.PublicId]); var before = await Snapshot(s); var fault = new Fail(stage);
        var result = await new CollectionCreateStore(new ProfileSetup.Factory(fixture, fault)).CreateAsync(new(s.Actor, s.Verifier, input), default);
        Assert.True(fault.Triggered); Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s)); await Success(s, input, before, await Create(s, input));
    }

    [FunctionalTheory][InlineData("account")][InlineData("session")][InlineData("profile")][InlineData("collection")][InlineData("folder")][InlineData("record")]
    public async Task GivenNaturalExpiryDuringEachLockWait_WhenCreating_ThenDenyBeforeDuplicateAndLeaveGraphUnchanged(string kind)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var p = await Profile(s, owner, scoped); var f = await Folder(s); var r = await Record(s, f.Id); var c = await Collection(s);
        await Routes(s, p.Id, c.Id, [], [], [f.Id], [r.Id]); s = s with { Verifier = await Selected(s, p.Id) }; var input = Input([p.PublicId], [f.PublicId], [r.PublicId]) with { CollectionId = c.PublicId };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2); await using (var db = fixture.CreateContext()) await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ExpiresAt, deadline));
        var before = await Snapshot(s); await using var held = fixture.CreateContext(); await using var tx = await held.Database.BeginTransactionAsync();
        var table = kind == "session" ? "vault_access_session" : kind; var id = kind switch { "account" => s.InternalId, "profile" => p.Id, "collection" => c.Id, "folder" => f.Id, _ => r.Id };
        await held.Database.ExecuteSqlRawAsync(kind == "session" ? "SELECT FROM cerberus.vault_access_session WHERE handle_verifier={0} FOR UPDATE" : "SELECT FROM cerberus." + table + " WHERE id={0} FOR UPDATE", kind == "session" ? (object)s.Verifier : id);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = new CollectionCreateStore(new ProfileSetup.Factory(fixture, new Signal(reached, table))).CreateAsync(new(s.Actor, s.Verifier, input), ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(pending.IsCompleted); await Past(deadline); await tx.CommitAsync();
        var result = await pending; Assert.Equal("vault_access_denied", result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("expiry", "vault_access_denied")][InlineData("policy", "vault_access_denied")][InlineData("generation", "vault_access_denied")][InlineData("revoked", "vault_access_denied")]
    [InlineData("actorTerminal", "not_found")][InlineData("profileTerminal", "vault_access_denied")][InlineData("folderTerminal", "not_found")][InlineData("recordTerminal", "not_found")]
    [InlineData("newTerminal", "revision_conflict")][InlineData("removePC", "not_found")][InlineData("removeCF", "not_found")][InlineData("removeCR", "not_found")]
    [InlineData("folderTrash", "not_found")][InlineData("recordTrash", "not_found")][InlineData("ancestry", "not_found")]
    [InlineData("profileTrash", "vault_access_denied")][InlineData("collectionTrash", "not_found")][InlineData("collectionTerminal", "not_found")]
    [InlineData("removePF", "not_found")][InlineData("removePR", "not_found")]
    public async Task GivenAuthorityChangesInsideFinalInsertBoundary_WhenCreating_ThenCurrentPermissionWinsAndTransactionRollsBack(string kind, string error)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var p = await Profile(s, owner, scoped); var root = await Folder(s); var f = await Folder(s, root.Id); var r = await Record(s); var c = await Collection(s); var hidden = await Folder(s);
        await Routes(s, p.Id, c.Id, kind == "removePF" ? [root.Id] : [], kind == "removePR" ? [r.Id] : [], kind == "removePF" ? [] : [root.Id], kind == "removePR" ? [] : [r.Id]); s = s with { Verifier = await Selected(s, p.Id) };
        await using (var db = fixture.CreateContext()) await db.Folders.Where(x => x.Id == hidden.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow));
        var input = Input([p.PublicId], [f.PublicId], [r.PublicId]); var before = await Snapshot(s);
        var hook = new BeforeInsert(async command =>
        {
            await using var write = command.Connection!.CreateCommand(); write.Transaction = command.Transaction;
            write.CommandText = kind switch
            {
                "expiry" => "UPDATE cerberus.vault_access_session SET expires_at=statement_timestamp()-interval '1 second' WHERE handle_verifier=@verifier",
                "policy" => "UPDATE cerberus.account SET policy_revision=policy_revision+1 WHERE id=@account",
                "generation" => "UPDATE cerberus.account SET revocation_generation=revocation_generation+1 WHERE id=@account",
                "revoked" => "UPDATE cerberus.vault_access_session SET revoked=true WHERE handle_verifier=@verifier",
                "removePC" => "DELETE FROM cerberus.profile_collection WHERE profile_id=@profile AND collection_id=@collection",
                "removeCF" => "DELETE FROM cerberus.collection_folder WHERE collection_id=@collection AND folder_id=@root",
                "removeCR" => "DELETE FROM cerberus.collection_record WHERE collection_id=@collection AND record_id=@record",
                "removePF" => "DELETE FROM cerberus.profile_folder WHERE profile_id=@profile AND folder_id=@root",
                "removePR" => "DELETE FROM cerberus.profile_record WHERE profile_id=@profile AND record_id=@record",
                "folderTrash" => "UPDATE cerberus.folder SET deleted_at=statement_timestamp() WHERE id=@folder",
                "recordTrash" => "UPDATE cerberus.record SET deleted_at=statement_timestamp() WHERE id=@record",
                "profileTrash" => "UPDATE cerberus.profile SET deleted_at=statement_timestamp() WHERE id=@profile",
                "collectionTrash" => "UPDATE cerberus.collection SET deleted_at=statement_timestamp() WHERE id=@collection",
                "ancestry" => "UPDATE cerberus.folder SET parent_folder_id=@hidden WHERE id=@root",
                _ => "INSERT INTO cerberus.terminal_erasure(resource_id,resource_kind,deleted_at) VALUES(@terminal,@kind,statement_timestamp())"
            };
            write.Parameters.Add(new NpgsqlParameter("verifier", s.Verifier)); write.Parameters.Add(new NpgsqlParameter("account", s.InternalId)); write.Parameters.Add(new NpgsqlParameter("profile", p.Id));
            write.Parameters.Add(new NpgsqlParameter("collection", c.Id)); write.Parameters.Add(new NpgsqlParameter("folder", f.Id)); write.Parameters.Add(new NpgsqlParameter("root", root.Id)); write.Parameters.Add(new NpgsqlParameter("record", r.Id)); write.Parameters.Add(new NpgsqlParameter("hidden", hidden.Id));
            write.Parameters.Add(new NpgsqlParameter("terminal", kind == "actorTerminal" ? s.AccountId : kind == "profileTerminal" ? p.PublicId : kind == "folderTerminal" ? f.PublicId : kind == "recordTerminal" ? r.PublicId : kind == "collectionTerminal" ? c.PublicId : input.CollectionId));
            write.Parameters.Add(new NpgsqlParameter("kind", kind == "actorTerminal" ? "account" : kind == "profileTerminal" ? "profile" : kind == "folderTerminal" ? "folder" : kind == "recordTerminal" ? "record" : "collection"));
            await write.ExecuteNonQueryAsync();
        });
        var result = await new CollectionCreateStore(new ProfileSetup.Factory(fixture, hook)).CreateAsync(new(s.Actor, s.Verifier, input), default);
        Assert.True(hook.Triggered); Assert.Equal(error, result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("minimum")][InlineData("firstSubmicro")][InlineData("precision")][InlineData("pre2000")][InlineData("pre2000Last")][InlineData("maximum")]
    public async Task GivenSupportedUtcEditTime_WhenCreating_ThenFloorBeforePostgresBinding(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var time = kind switch
        {
            "minimum" => new DateTimeOffset(10, TimeSpan.Zero), "firstSubmicro" => new DateTimeOffset(19, TimeSpan.Zero), "precision" => DateTimeOffset.UnixEpoch.AddTicks(19),
            "pre2000" => new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(19), "pre2000Last" => new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(-1), _ => DateTimeOffset.MaxValue
        };
        var input = Input() with { EditedAt = time }; var before = await Snapshot(s); await Success(s, input, before, await Create(s, input));
    }

    [FunctionalTheory][InlineData("rootExhausted", "revision_conflict")][InlineData("parentExhausted", "revision_conflict")][InlineData("regressed", "persistence_unavailable")]
    public async Task GivenSequenceFailureAfterAllocation_WhenCreating_ThenEntireGraphRollsBack(string kind, string error)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var p = await Profile(s, owner, scoped); var before = await Snapshot(s);
        await using var db = fixture.CreateContext(); var previous = await db.Database.SqlQuery<long>($"SELECT last_value AS \"Value\" FROM cerberus.server_sequence").SingleAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{(kind == "rootExhausted" ? ProtocolBinary.MaxInteger : kind == "parentExhausted" ? ProtocolBinary.MaxInteger - 1 : 1)},true)");
        try { var result = await Create(s, Input([p.PublicId])); Assert.Equal(error, result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s)); }
        finally { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{previous},true)"); }
    }

    [FunctionalTheory][InlineData("PF")][InlineData("PR")][InlineData("PC")][InlineData("CF")][InlineData("CR")][InlineData("ancestry")][InlineData("memberEdit")]
    public async Task GivenCapturedRoutesChangeBeforeMemberLocks_WhenCreating_ThenReloadAndPreserveWinningGraph(string kind)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var p = await Profile(s, owner, scoped); var f = await Folder(s); var ancestor = await Folder(s); var r = await Record(s, f.Id); var c = await Collection(s);
        var input = Input([p.PublicId], [f.PublicId], [r.PublicId]); string? winner = null;
        var hook = new BeforeTable("folder", async () =>
        {
            await using var db = fixture.CreateContext();
            if (kind == "PF") db.ProfileFolders.Add(new() { AccountId = s.InternalId, ProfileId = p.Id, FolderId = f.Id });
            if (kind == "PR") db.ProfileRecords.Add(new() { AccountId = s.InternalId, ProfileId = p.Id, RecordId = r.Id });
            if (kind == "PC") { await Members(s, c.Id, [f.Id], []); db.ProfileCollections.Add(new() { ProfileId = p.Id, CollectionId = c.Id }); }
            if (kind == "CF") db.CollectionFolders.Add(new() { AccountId = s.InternalId, CollectionId = c.Id, FolderId = f.Id });
            if (kind == "CR") db.CollectionRecords.Add(new() { AccountId = s.InternalId, CollectionId = c.Id, RecordId = r.Id });
            if (kind == "ancestry") await db.Folders.Where(x => x.Id == f.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ParentFolderId, ancestor.Id));
            if (kind == "memberEdit") await db.Records.Where(x => x.Id == r.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Envelope, Bytes(ProfileSetup.Envelope())).SetProperty(v => v.Revision, 2));
            await db.SaveChangesAsync(); winner = await Snapshot(s);
        });
        var result = await new CollectionCreateStore(new ProfileSetup.Factory(fixture, hook)).CreateAsync(new(s.Actor, s.Verifier, input), default);
        Assert.True(hook.Triggered); Assert.NotNull(winner); Assert.Equal("revision_conflict", result.Error); Assert.Null(result.Data); Assert.Equal(winner, await Snapshot(s));
        await Success(s, input, winner!, await Create(s, input));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenExpiryAtOrAfterGuardedInsert_WhenCreating_ThenAuthorizeOnlyAtStatementBoundary(bool after)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var input = Input(); var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        await using (var db = fixture.CreateContext()) await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ExpiresAt, deadline));
        var before = await Snapshot(s); var hook = new ExpireAtInsert(deadline, after);
        var result = await new CollectionCreateStore(new ProfileSetup.Factory(fixture, hook)).CreateAsync(new(s.Actor, s.Verifier, input), default); Assert.True(hook.Triggered);
        if (after) await Success(s, input, before, result); else { Assert.Equal("vault_access_denied", result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s)); }
    }

    [FunctionalTheory]
    [InlineData("folderCreate", false)][InlineData("folderCreate", true)][InlineData("recordCreate", false)][InlineData("recordCreate", true)]
    [InlineData("folderMove", false)][InlineData("folderMove", true)][InlineData("recordMove", false)][InlineData("recordMove", true)]
    [InlineData("folderTrash", false)][InlineData("folderTrash", true)][InlineData("recordTrash", false)][InlineData("recordTrash", true)]
    [InlineData("association", false)][InlineData("association", true)]
    public async Task GivenActualOwnerWriterAndCollectionCreate_WhenRacingInBothOrders_ThenSerializeWithoutLostWinningState(string writer, bool createFirst)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var p = await Profile(s, owner, scoped); var f = await Folder(s); var r = await Record(s); var destination = await Folder(s);
        var input = Input([p.PublicId], [f.PublicId], [r.PublicId]); var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        async Task<string?> Write(IDbContextFactory<AppDbContext> factory) => writer switch
        {
            "folderCreate" => (await new FolderCreateStore(factory).CreateAsync(new(s.Actor, s.Verifier, new(Guid.NewGuid(), ProfileSetup.Envelope(), DateTimeOffset.UnixEpoch, [], f.PublicId)), ct.Token)).Error,
            "recordCreate" => (await new RecordCreateStore(factory).CreateAsync(new(s.Actor, s.Verifier, new(Guid.NewGuid(), ProfileSetup.Envelope(), DateTimeOffset.UnixEpoch, [], f.PublicId)), ct.Token)).Error,
            "folderMove" => (await new FolderMoveStore(factory).MoveAsync(new(s.Actor, s.Verifier, f.PublicId, 1, destination.PublicId), ct.Token)).Error,
            "recordMove" => (await new RecordMoveStore(factory).MoveAsync(new(s.Actor, s.Verifier, r.PublicId, 1, destination.PublicId), ct.Token)).Error,
            "folderTrash" => (await new FolderTrashStore(factory).TrashAsync(new(s.Actor, s.Verifier, f.PublicId, 1), ct.Token)).Error,
            "recordTrash" => (await new RecordTrashStore(factory).TrashAsync(new(s.Actor, s.Verifier, r.PublicId, 1), ct.Token)).Error,
            _ => (await new ProfileAssociationStore(factory).SetAsync(new(s.Actor, s.Verifier, p.PublicId, new(1, [], [], [])), ct.Token)).Error
        };
        Task<string?> write; Task<VaultResult<CollectionCreateDetails>> create;
        if (createFirst)
        {
            create = new CollectionCreateStore(new ProfileSetup.Factory(fixture, new PauseAccount(reached, release))).CreateAsync(new(s.Actor, s.Verifier, input), ct.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            write = Write(new ProfileSetup.Factory(fixture, new Signal(blocked, "account")));
        }
        else
        {
            write = Write(new ProfileSetup.Factory(fixture, new PauseAccount(reached, release))); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            create = new CollectionCreateStore(new ProfileSetup.Factory(fixture, new Signal(blocked, "account"))).CreateAsync(new(s.Actor, s.Verifier, input), ct.Token);
        }
        try { await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(createFirst ? write.IsCompleted : create.IsCompleted); } finally { release.TrySetResult(); }
        await Task.WhenAll(create, write);
        Assert.Equal(createFirst && writer is not ("folderCreate" or "recordCreate") ? "revision_conflict" : null, write.Result);
        Assert.Equal(!createFirst && writer is "folderTrash" or "recordTrash" ? "not_found" : null, create.Result.Error);
        await using var db = fixture.CreateContext(); var created = await db.Collections.AsNoTracking().SingleOrDefaultAsync(x => x.PublicId == input.CollectionId);
        if (create.Result.Error is null)
        {
            Assert.NotNull(created); Assert.Equal(Bytes(input.Envelope), created.Envelope); Assert.Equal(1, created.Revision);
            Assert.True(await db.ProfileCollections.AnyAsync(x => x.CollectionId == created.Id && x.ProfileId == p.Id)); Assert.True(await db.CollectionFolders.AnyAsync(x => x.CollectionId == created.Id && x.FolderId == f.Id)); Assert.True(await db.CollectionRecords.AnyAsync(x => x.CollectionId == created.Id && x.RecordId == r.Id));
            Assert.Equal(2 + (!createFirst && writer == "association" ? 1 : 0), (await db.Profiles.SingleAsync(x => x.Id == p.Id)).Revision);
        }
        else { Assert.Null(created); Assert.True(writer == "folderTrash" ? (await db.Folders.SingleAsync(x => x.Id == f.Id)).DeletedAt is not null : (await db.Records.SingleAsync(x => x.Id == r.Id)).DeletedAt is not null); }
        if (writer == "folderMove") Assert.Equal(createFirst ? null : destination.Id, (await FolderRow(f.Id)).ParentFolderId);
        if (writer == "recordMove") Assert.Equal(createFirst ? null : destination.Id, (await RecordRow(r.Id)).FolderId);
    }

    [FunctionalTheory][InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task GivenActualNativeRecipientEditor_WhenRacingCollectionCreate_ThenCollectionBeforeContentLocksPreserveWinner(bool record, bool createFirst)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var recipient = await ProfileSetup.Create(fixture, client); var items = await AssociationSetup.Items(fixture, s);
        await Members(s, items.Collection.Id, [items.Folder.Id], [items.Record.Id]); await AssociationSetup.Grant(fixture, s, owner, recipient, client, items.Collection, CollectionGrantAccess.ReadWrite);
        var input = Input(folders: record ? [] : [items.Folder.PublicId], records: record ? [items.Record.PublicId] : []); var envelope = ProfileSetup.Envelope();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        async Task<string?> Edit(IDbContextFactory<AppDbContext> f) => record ? (await new RecordUpdateStore(f).UpdateAsync(new(recipient.Actor, recipient.Verifier, items.Record.PublicId, new(1, envelope, DateTimeOffset.UnixEpoch.AddDays(1))), ct.Token)).Error : (await new FolderUpdateStore(f).UpdateAsync(new(recipient.Actor, recipient.Verifier, items.Folder.PublicId, new(1, envelope, DateTimeOffset.UnixEpoch.AddDays(1))), ct.Token)).Error;
        Task<VaultResult<CollectionCreateDetails>> create; Task<string?> edit;
        if (createFirst)
        {
            create = new CollectionCreateStore(new ProfileSetup.Factory(fixture, new BeforeInsert(async _ => { reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }))).CreateAsync(new(s.Actor, s.Verifier, input), ct.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            edit = Edit(new ProfileSetup.Factory(fixture, new Signal(blocked, "collection")));
        }
        else
        {
            edit = Edit(new ProfileSetup.Factory(fixture, new PauseContent(reached, release))); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            create = new CollectionCreateStore(new ProfileSetup.Factory(fixture, new Signal(blocked, "collection"))).CreateAsync(new(s.Actor, s.Verifier, input), ct.Token);
        }
        try { await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(createFirst ? edit.IsCompleted : create.IsCompleted); } finally { release.TrySetResult(); }
        await Task.WhenAll(create, edit); Assert.Equal(createFirst ? "revision_conflict" : null, edit.Result); Assert.Equal(createFirst ? null : "revision_conflict", create.Result.Error);
        var bytes = record ? (await RecordRow(items.Record.Id)).Envelope : (await FolderRow(items.Folder.Id)).Envelope;
        Assert.Equal(createFirst ? record ? items.Record.Envelope : items.Folder.Envelope : Bytes(envelope), bytes);
        Assert.Equal(2, record ? (await RecordRow(items.Record.Id)).Revision : (await FolderRow(items.Folder.Id)).Revision);
        await using var db = fixture.CreateContext(); Assert.Equal(createFirst, await db.Collections.AnyAsync(x => x.PublicId == input.CollectionId));
    }

    [FunctionalTheory][InlineData("complete", null)][InlineData("omit", "validation_failed")][InlineData("extra", "validation_failed")][InlineData("foreign", "validation_failed")][InlineData("stale", "revision_conflict")][InlineData("typedAlias", null)]
    public async Task GivenNewCollection_WhenNativeRotatingCompleteInventory_ThenIncludeExactCurrentTypedResourceBeforeConsumingProof(string kind, string? error)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var f = await Folder(s); var r = await Record(s); var input = Input() with { CollectionId = kind == "typedAlias" ? r.PublicId : Guid.NewGuid() };
        if (kind == "typedAlias") { await using var update = fixture.CreateContext(); await update.Folders.Where(x => x.Id == f.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.PublicId, r.PublicId)); f = await FolderRow(f.Id); }
        Assert.Null((await Create(s, input)).Error); var replacements = new List<ContentReplacement> { new("account", s.AccountId, 1, ProfileSetup.Envelope(2)), new("folder", f.PublicId, 1, ProfileSetup.Envelope(2)), new("record", r.PublicId, 1, ProfileSetup.Envelope(2)) };
        if (kind != "omit") replacements.Add(new("collection", kind == "foreign" ? Guid.NewGuid() : input.CollectionId, kind == "stale" ? 2 : 1, ProfileSetup.Envelope(2)));
        if (kind == "extra") replacements.Add(new("collection", Guid.NewGuid(), 1, ProfileSetup.Envelope(2)));
        var change = new ProtectionChange(s.AccountId, 1, 1, "rotate-content", owner.Rewrap(), replacements.ToArray()); var raw = Bytes(change);
        var challenge = (await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor, "change-protection", ProtocolBinary.Encode(SHA256.HashData(raw)), default)).Data!;
        var before = await Snapshot(s); var result = await new VaultProtectionChangeStore(fixture).ChangeAsync(new(s.Actor, s.Verifier, challenge.ChallengeId, owner.Sign(challenge), raw, change), default); Assert.Equal(error, result.Error);
        await using var db = fixture.CreateContext(); Assert.Equal(error is null, (await db.VaultUnlockChallenges.SingleAsync(x => x.PublicId == challenge.ChallengeId)).Consumed);
        if (error is not null) Assert.Equal(before, await Snapshot(s));
        else { var row = await db.Collections.SingleAsync(x => x.PublicId == input.CollectionId); Assert.Equal(2, row.KeyEpoch); Assert.Equal(2, row.Revision); Assert.Equal(Bytes(replacements.Single(x => x.ResourceKind == "collection").Envelope), row.Envelope); Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(10), row.EditedAt); }
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenActualOwnerRotationAndCollectionCreation_WhenRacing_ThenStaleHandleOrInventoryFailsWithoutPartialState(bool rotateFirst)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var input = Input(); var change = new ProtectionChange(s.AccountId, 1, 1, "rotate-content", owner.Rewrap(), [new("account", s.AccountId, 1, ProfileSetup.Envelope(2))]); var raw = Bytes(change);
        var challenge = (await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor, "change-protection", ProtocolBinary.Encode(SHA256.HashData(raw)), default)).Data!;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Task<VaultResult<CollectionCreateDetails>> create; Task<VaultResult<ProtectionChangeDetails>> rotation;
        if (rotateFirst)
        {
            rotation = new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture, new PauseAccount(reached, release))).ChangeAsync(new(s.Actor, s.Verifier, challenge.ChallengeId, owner.Sign(challenge), raw, change), ct.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            create = new CollectionCreateStore(new ProfileSetup.Factory(fixture, new Signal(blocked, "account"))).CreateAsync(new(s.Actor, s.Verifier, input), ct.Token);
        }
        else
        {
            create = new CollectionCreateStore(new ProfileSetup.Factory(fixture, new PauseAccount(reached, release))).CreateAsync(new(s.Actor, s.Verifier, input), ct.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            rotation = new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture, new Signal(blocked, "account"))).ChangeAsync(new(s.Actor, s.Verifier, challenge.ChallengeId, owner.Sign(challenge), raw, change), ct.Token);
        }
        try { await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(rotateFirst ? create.IsCompleted : rotation.IsCompleted); } finally { release.TrySetResult(); }
        await Task.WhenAll(create, rotation); Assert.Equal(rotateFirst ? null : "validation_failed", rotation.Result.Error); Assert.Equal(rotateFirst ? "vault_access_denied" : null, create.Result.Error);
        await using var db = fixture.CreateContext(); Assert.Equal(rotateFirst, (await db.VaultUnlockChallenges.SingleAsync(x => x.PublicId == challenge.ChallengeId)).Consumed); Assert.Equal(!rotateFirst, await db.Collections.AnyAsync(x => x.PublicId == input.CollectionId));
    }

    [FunctionalFact]
    public async Task GivenActualPriorIndependentRecordAndFolderTrash_WhenCreating_ThenPreserveOperationsDeadlinesAndCiphertext()
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var folder = await Folder(s); var record = await Record(s, folder.Id);
        Assert.Null((await new RecordTrashStore(fixture).TrashAsync(new(s.Actor, s.Verifier, record.PublicId, 1), default)).Error);
        Assert.Null((await new FolderTrashStore(fixture).TrashAsync(new(s.Actor, s.Verifier, folder.PublicId, 2), default)).Error);
        var member = await Folder(s); var input = Input(folders: [member.PublicId]); var before = await Snapshot(s); await Success(s, input, before, await Create(s, input));
    }

    [FunctionalTheory][InlineData("cancel")][InlineData("timeout")][InlineData("db")][InlineData("dbUpdate")][InlineData("wrapped")]
    public async Task GivenNecessaryPersistenceFailure_WhenCreating_ThenSafe503WithoutPrivateDetails(string kind)
    {
        Exception exception = kind switch { "cancel" => new OperationCanceledException(), "timeout" => new TimeoutException("private"), "db" => new NpgsqlException("private"), "dbUpdate" => new DbUpdateException("private"), _ => new InvalidOperationException("private", new TimeoutException()) };
        var result = await new CollectionCreateStore(new ThrowingFactory(exception)).CreateAsync(new(Guid.NewGuid(), new string('a', 64), Input()), default);
        Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data);
    }

    [FunctionalTheory][InlineData("actor")][InlineData("id")][InlineData("null")][InlineData("time")][InlineData("profiles")][InlineData("records")][InlineData("folders")][InlineData("epoch")]
    public async Task GivenInvalidInternalInput_WhenCreating_ThenRejectBeforeUnavailableDependency(string kind)
    {
        var input = Input(); input = kind switch { "id" => input with { CollectionId = Guid.Empty }, "null" => null!, "time" => input with { EditedAt = new(9, TimeSpan.Zero) }, "profiles" => input with { ProfileIds = null! }, "records" => input with { RecordIds = null! }, "folders" => input with { FolderIds = null! }, "epoch" => input with { Envelope = input.Envelope with { KeyEpoch = 2 } }, _ => input };
        var result = await new CollectionCreateStore(new ThrowingFactory(new TimeoutException())).CreateAsync(new(kind == "actor" ? Guid.Empty : Guid.NewGuid(), new string('a', 64), input), default); Assert.Equal(kind == "actor" ? "authentication_required" : "validation_failed", result.Error); Assert.Null(result.Data);
    }

    [FunctionalFact]
    public async Task GivenCanceledCaller_WhenCreating_ThenPropagateBeforeDependency()
    { using var ct = new CancellationTokenSource(); ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionCreateStore(new ThrowingFactory(new TimeoutException())).CreateAsync(new(Guid.NewGuid(), new string('a', 64), Input()), ct.Token)); }

    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly)][InlineData(CollectionGrantAccess.ReadWrite)]
    public async Task GivenExistingNativeGrant_WhenCreatingPrivateOwnedGrouping_ThenPreserveGrantsKeysAndRecipientGraph(CollectionGrantAccess access)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var recipient = await ProfileSetup.Create(fixture, client);
        var items = await AssociationSetup.Items(fixture, s); await Members(s, items.Collection.Id, [items.Folder.Id], [items.Record.Id]); await AssociationSetup.Grant(fixture, s, owner, recipient, client, items.Collection, access);
        var p = await Profile(s, owner, scoped); await Routes(s, p.Id, items.Collection.Id, [], [], [], []); s = s with { Verifier = await Selected(s, p.Id) };
        Assert.Null((await new FolderReadStore(fixture).ReadAsync(new(recipient.Actor, recipient.Verifier, items.Folder.PublicId), default)).Error); Assert.Null((await new RecordReadStore(fixture).ReadAsync(new(recipient.Actor, recipient.Verifier, items.Record.PublicId), default)).Error);
        var before = await Snapshot(s); var other = await Snapshot(recipient); var input = Input([p.PublicId], [items.Folder.PublicId], [items.Record.PublicId]); await Success(s, input, before, await Create(s, input)); Assert.Equal(other, await Snapshot(recipient));
        Assert.Null((await new FolderReadStore(fixture).ReadAsync(new(recipient.Actor, recipient.Verifier, items.Folder.PublicId), default)).Error); Assert.Null((await new RecordReadStore(fixture).ReadAsync(new(recipient.Actor, recipient.Verifier, items.Record.PublicId), default)).Error);
    }

    [FunctionalFact]
    public async Task GivenNewCollectionWithAllInitialLinks_WhenNativeRewrapping_ThenPreserveExactCollectionAndRelationships()
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var p = await Profile(s, owner, scoped); var f = await Folder(s); var r = await Record(s, f.Id);
        Assert.Null((await Create(s, Input([p.PublicId], [f.PublicId], [r.PublicId]))).Error); var before = JsonNode.Parse(await Snapshot(s))!.AsObject();
        var change = new ProtectionChange(s.AccountId, 1, 1, "rewrap", owner.Rewrap(), []); var raw = Bytes(change); var challenge = (await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor, "change-protection", ProtocolBinary.Encode(SHA256.HashData(raw)), default)).Data!;
        Assert.Null((await new VaultProtectionChangeStore(fixture).ChangeAsync(new(s.Actor, s.Verifier, challenge.ChallengeId, owner.Sign(challenge), raw, change), default)).Error);
        var current = JsonNode.Parse(await Snapshot(s))!.AsObject(); foreach (var table in new[] { "Profiles", "Folders", "Records", "Collections", "ProfileCollections", "CollectionFolders", "CollectionRecords" }) Assert.Equal(before[table]!.ToJsonString(), current[table]!.ToJsonString());
    }

    [FunctionalFact]
    public async Task GivenLargeUnrelatedInventory_WhenCreating_ThenBoundRequestedAncestryAndLockOnlyExplicitMembersInOrder()
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var p = await Profile(s, owner, scoped); var root = await Folder(s); var f = await Folder(s, root.Id); var r = await Record(s, f.Id); var c = await Collection(s);
        await Routes(s, p.Id, c.Id, [root.Id], [], [f.Id], []); s = s with { Verifier = await Selected(s, p.Id) };
        await using (var db = fixture.CreateContext())
        {
            db.Folders.AddRange(Enumerable.Range(0, 200).Select(_ => new VaultFolder { PublicId = Guid.NewGuid(), AccountId = s.InternalId, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch }));
            db.Records.AddRange(Enumerable.Range(0, 200).Select(_ => new VaultRecord { PublicId = Guid.NewGuid(), AccountId = s.InternalId, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch })); await db.SaveChangesAsync();
        }
        var capture = new Capture(fixture); var input = Input([p.PublicId], [f.PublicId], [r.PublicId]); var before = await Snapshot(s);
        await Success(s, input, before, await new CollectionCreateStore(new ProfileSetup.Factory(fixture, capture)).CreateAsync(new(s.Actor, s.Verifier, input), default));
        Assert.Equal(new[] { "account", "vault_access_session", "profile", "collection", "folder", "record" }, capture.Locks.Select(x => x.Table));
        Assert.Equal(new[] { p.Id }, capture.Locks.Single(x => x.Table == "profile").Ids); Assert.Equal(new[] { c.Id }, capture.Locks.Single(x => x.Table == "collection").Ids);
        Assert.Equal(new[] { f.Id }, capture.Locks.Single(x => x.Table == "folder").Ids); Assert.Equal(new[] { r.Id }, capture.Locks.Single(x => x.Table == "record").Ids);
        Assert.NotNull(capture.Plan); using var document = JsonDocument.Parse(capture.Plan!); var nodes = Nodes(document.RootElement[0].GetProperty("Plan")).ToArray();
        foreach (var (name, maximum) in new[] { ("folder_candidates", 1), ("record_candidates", 1), ("paths", 4) })
        {
            var producers = nodes.Where(x => x.TryGetProperty("Subplan Name", out var value) && value.GetString() == "CTE " + name).ToArray(); Assert.Single(producers); Assert.InRange(producers[0].GetProperty("Actual Rows").GetDecimal(), 1, maximum);
        }
    }

    private sealed class Capture(PostgresFixture fixture) : DbCommandInterceptor
    {
        public List<(string Table, long[] Ids)> Locks { get; } = []; public string? Plan { get; private set; }
        private void Note(DbCommand c)
        {
            if (!c.CommandText.Contains("FOR ")) return;
            var table = new[] { "account", "vault_access_session", "profile", "collection", "folder", "record" }.Single(x => c.CommandText.Contains("FROM cerberus." + x + " "));
            Locks.Add((table, c.Parameters.Cast<NpgsqlParameter>().Select(x => x.Value).OfType<long[]>().SingleOrDefault() ?? []));
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default) { Note(c); return ValueTask.FromResult(r); }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
        {
            Note(c); if (Plan is null && c.CommandText.StartsWith("WITH RECURSIVE"))
            { await using var db = fixture.CreateContext(); await db.Database.OpenConnectionAsync(ct); await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + c.CommandText; foreach (var p in c.Parameters.Cast<NpgsqlParameter>()) command.Parameters.Add(p.Clone()); Plan = (string)(await command.ExecuteScalarAsync(ct))!; await File.WriteAllTextAsync("/tmp/cerberus-uc29-explain.json", Plan, ct); }
            return r;
        }
    }
    private static IEnumerable<JsonElement> Nodes(JsonElement n) { yield return n; if (n.TryGetProperty("Plans", out var plans)) foreach (var p in plans.EnumerateArray()) foreach (var item in Nodes(p)) yield return item; }
    [FunctionalTheory]
    [InlineData("profile", "zero")][InlineData("folder", "zero")][InlineData("record", "zero")]
    [InlineData("profile", "unsafe")][InlineData("folder", "unsafe")][InlineData("record", "unsafe")]
    [InlineData("profile", "time")][InlineData("folder", "time")][InlineData("record", "time")]
    [InlineData("profile", "purge")][InlineData("folder", "purge")][InlineData("record", "purge")]
    [InlineData("profile", "max")][InlineData("folder", "max")][InlineData("record", "max")]
    [InlineData("profile", "valid")][InlineData("folder", "valid")][InlineData("record", "valid")]
    public async Task GivenStructuralStateChangesAtGuardedInsert_WhenCreating_ThenClassifyCurrentCorruptionOrRetryAndRollback(string kind, string field)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var p = await Profile(s, owner, scoped); var f = await Folder(s); var r = await Record(s);
        var before = await Snapshot(s); var input = Input([p.PublicId], [f.PublicId], [r.PublicId]); var hook = new BeforeInsert(async command =>
        {
            await using var write = command.Connection!.CreateCommand(); write.Transaction = command.Transaction;
            var value = field switch { "zero" => "revision=0", "unsafe" => "server_sequence=9007199254740992", "time" => "edited_at='-infinity'", "purge" => "purge_at=statement_timestamp()", "max" => "revision=9007199254740991", _ => "revision=revision+1" };
            write.CommandText = "UPDATE cerberus." + kind + " SET " + value + " WHERE id=@id"; write.Parameters.Add(new NpgsqlParameter("id", kind == "profile" ? p.Id : kind == "folder" ? f.Id : r.Id)); await write.ExecuteNonQueryAsync();
        });
        var result = await new CollectionCreateStore(new ProfileSetup.Factory(fixture, hook)).CreateAsync(new(s.Actor, s.Verifier, input), default);
        Assert.True(hook.Triggered); Assert.Equal(field is "valid" or "max" ? "revision_conflict" : "persistence_unavailable", result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s));
    }

    private CollectionCreateStore Store() => new(fixture);
    private Task<VaultResult<CollectionCreateDetails>> Create(ProfileSetup.State s, CollectionCreateInput input) => Store().CreateAsync(new(s.Actor, s.Verifier, input), default);
    private static CollectionCreateInput Input(Guid[]? profiles = null, Guid[]? folders = null, Guid[]? records = null) => new(Guid.NewGuid(), ProfileSetup.Envelope(), DateTimeOffset.UnixEpoch.AddTicks(19), profiles ?? [], records ?? [], folders ?? []);
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ProtectionFixture.Json);
    private async Task<Profile> Profile(ProfileSetup.State s, ProtectionFixture owner, ProtectionFixture scoped)
    { var input = ProfileSetup.Input(s, owner, scoped); Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor, s.Verifier, input), default)).Error); await using var db = fixture.CreateContext(); return await db.Profiles.AsNoTracking().SingleAsync(x => x.PublicId == input.ProfileId); }
    private async Task<VaultFolder> Folder(ProfileSetup.State s, long? parent = null)
    { var row = new VaultFolder { PublicId = Guid.NewGuid(), AccountId = s.InternalId, ParentFolderId = parent, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch }; await using var db = fixture.CreateContext(); db.Folders.Add(row); await db.SaveChangesAsync(); return row; }
    private async Task<VaultRecord> Record(ProfileSetup.State s, long? folder = null)
    { var row = new VaultRecord { PublicId = Guid.NewGuid(), AccountId = s.InternalId, FolderId = folder, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch }; await using var db = fixture.CreateContext(); db.Records.Add(row); await db.SaveChangesAsync(); return row; }
    private async Task<VaultCollection> Collection(ProfileSetup.State s)
    { var row = new VaultCollection { PublicId = Guid.NewGuid(), AccountId = s.InternalId, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch }; await using var db = fixture.CreateContext(); db.Collections.Add(row); await db.SaveChangesAsync(); return row; }
    private async Task<VaultFolder> FolderRow(long id) { await using var db = fixture.CreateContext(); return await db.Folders.AsNoTracking().SingleAsync(x => x.Id == id); }
    private async Task<VaultRecord> RecordRow(long id) { await using var db = fixture.CreateContext(); return await db.Records.AsNoTracking().SingleAsync(x => x.Id == id); }
    private async Task<string> Selected(ProfileSetup.State s, long profile)
    { var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); await using var db = fixture.CreateContext(); db.VaultAccessSessions.Add(new() { AccountId = s.InternalId, ProfileId = profile, HandleVerifier = verifier, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 }); await db.SaveChangesAsync(); return verifier; }
    private async Task Members(ProfileSetup.State s, long collection, long[] folders, long[] records)
    { await using var db = fixture.CreateContext(); db.CollectionFolders.AddRange(folders.Select(x => new CollectionFolder { AccountId = s.InternalId, CollectionId = collection, FolderId = x })); db.CollectionRecords.AddRange(records.Select(x => new CollectionRecord { AccountId = s.InternalId, CollectionId = collection, RecordId = x })); await db.SaveChangesAsync(); }
    private async Task Routes(ProfileSetup.State s, long profile, long collection, long[] folders, long[] records, long[] collectionFolders, long[] collectionRecords)
    {
        await using var db = fixture.CreateContext(); db.ProfileFolders.AddRange(folders.Select(x => new ProfileFolder { AccountId = s.InternalId, ProfileId = profile, FolderId = x })); db.ProfileRecords.AddRange(records.Select(x => new ProfileRecord { AccountId = s.InternalId, ProfileId = profile, RecordId = x }));
        db.ProfileCollections.Add(new() { ProfileId = profile, CollectionId = collection }); await db.SaveChangesAsync(); await Members(s, collection, collectionFolders, collectionRecords);
    }
    private async Task Success(ProfileSetup.State s, CollectionCreateInput input, string before, VaultResult<CollectionCreateDetails> result)
    {
        Assert.Null(result.Error); Assert.NotNull(result.Data); Assert.Equal(input.CollectionId, result.Data.CollectionId); Assert.Equal(1, result.Data.Revision); Assert.InRange(result.Data.ServerSequence, 1, ProtocolBinary.MaxInteger);
        Assert.Equal(input.EditedAt.Ticks - input.EditedAt.Ticks % 10, result.Data.EditedAt.Ticks); Assert.Equal(TimeSpan.Zero, result.Data.EditedAt.Offset);
        await using var db = fixture.CreateContext(); var row = await db.Collections.AsNoTracking().SingleAsync(x => x.PublicId == input.CollectionId);
        Assert.Equal(s.InternalId, row.AccountId); Assert.Equal(Bytes(input.Envelope), row.Envelope); Assert.Equal(1, row.KeyEpoch); Assert.Null(row.DeletedAt); Assert.Null(row.PurgeAt); Assert.Equal(result.Data.ServerSequence, row.ServerSequence); Assert.Equal(result.Data.EditedAt, row.EditedAt);
        Assert.Equal(input.ProfileIds.Order(), await (from pc in db.ProfileCollections join p in db.Profiles on pc.ProfileId equals p.Id where pc.CollectionId == row.Id orderby p.PublicId select p.PublicId).ToArrayAsync());
        Assert.Equal(input.FolderIds.Order(), await (from cf in db.CollectionFolders join f in db.Folders on cf.FolderId equals f.Id where cf.CollectionId == row.Id && cf.AccountId == s.InternalId orderby f.PublicId select f.PublicId).ToArrayAsync());
        Assert.Equal(input.RecordIds.Order(), await (from cr in db.CollectionRecords join r in db.Records on cr.RecordId equals r.Id where cr.CollectionId == row.Id && cr.AccountId == s.InternalId orderby r.PublicId select r.PublicId).ToArrayAsync());
        var old = JsonNode.Parse(before)!.AsObject(); var current = JsonNode.Parse(await Snapshot(s))!.AsObject();
        foreach (var table in new[] { "ProfileCollections", "CollectionFolders", "CollectionRecords", "Collections" })
        { var array = current[table]!.AsArray(); foreach (var item in array.ToArray()) if (item![table == "Collections" ? "Id" : "CollectionId"]!.GetValue<long>() == row.Id) array.Remove(item); }
        foreach (var (table, ids) in new[] { ("Profiles", input.ProfileIds), ("Folders", input.FolderIds), ("Records", input.RecordIds) })
        {
            foreach (var item in current[table]!.AsArray())
            {
                if (!ids.Contains(item!["PublicId"]!.GetValue<Guid>())) continue;
                var previous = old[table]!.AsArray().Single(x => x!["Id"]!.GetValue<long>() == item["Id"]!.GetValue<long>())!;
                Assert.Equal(previous["Revision"]!.GetValue<long>() + 1, item["Revision"]!.GetValue<long>()); Assert.True(item["ServerSequence"]!.GetValue<long>() > previous["ServerSequence"]!.GetValue<long>()); Assert.InRange(item["ServerSequence"]!.GetValue<long>(), 1, ProtocolBinary.MaxInteger); Assert.NotEqual(previous["ConcurrencyStamp"]!.ToJsonString(), item["ConcurrencyStamp"]!.ToJsonString());
                foreach (var key in new[] { "Revision", "ServerSequence", "ConcurrencyStamp" }) item[key] = previous[key]!.DeepClone();
            }
        }
        Assert.Equal(old.ToJsonString(), current.ToJsonString());
    }
    private async Task<string> Snapshot(ProfileSetup.State s)
    {
        await using var db = fixture.CreateContext(); return JsonSerializer.Serialize(new
        {
            Accounts = await db.Accounts.AsNoTracking().Where(x => x.Id == s.InternalId).ToArrayAsync(), Protection = await db.VaultProtections.AsNoTracking().Where(x => x.AccountId == s.InternalId).ToArrayAsync(),
            Profiles = await db.Profiles.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(), Folders = await db.Folders.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(), Records = await db.Records.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(), Collections = await db.Collections.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(),
            ProfileFolders = await db.ProfileFolders.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.ProfileId).ThenBy(x => x.FolderId).ToArrayAsync(), ProfileRecords = await db.ProfileRecords.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.ProfileId).ThenBy(x => x.RecordId).ToArrayAsync(), ProfileCollections = await db.ProfileCollections.AsNoTracking().Where(x => db.Profiles.Any(p => p.AccountId == s.InternalId && p.Id == x.ProfileId)).OrderBy(x => x.ProfileId).ThenBy(x => x.CollectionId).ToArrayAsync(),
            CollectionFolders = await db.CollectionFolders.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.CollectionId).ThenBy(x => x.FolderId).ToArrayAsync(), CollectionRecords = await db.CollectionRecords.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.CollectionId).ThenBy(x => x.RecordId).ToArrayAsync(),
            Grants = await db.CollectionGrants.AsNoTracking().Where(x => x.RecipientAccountId == s.InternalId || db.Collections.Any(c => c.AccountId == s.InternalId && c.Id == x.CollectionId)).OrderBy(x => x.Id).ToArrayAsync(), Sessions = await db.VaultAccessSessions.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(),
            Operations = await db.TrashOperations.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(), Entries = await db.TrashEntries.AsNoTracking().Where(x => db.TrashOperations.Any(o => o.AccountId == s.InternalId && o.Id == x.OperationId)).OrderBy(x => x.Id).ToArrayAsync(), Queue = await db.RetentionWorkItems.AsNoTracking().Where(x => db.TrashOperations.Any(o => o.AccountId == s.InternalId && x.OperationKey == "trash/" + o.PublicId)).OrderBy(x => x.Id).ToArrayAsync()
        });
    }
    private static async Task Past(DateTimeOffset time) { var delay = time - DateTimeOffset.UtcNow; if (delay > TimeSpan.Zero) await Task.Delay(delay + TimeSpan.FromMilliseconds(100)); }
    private sealed class BeforeInsert(Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (!Triggered && command.CommandText.Contains("INSERT INTO cerberus.collection(")) { Triggered = true; await action(command); } return result; }
    }
    private sealed class Signal(TaskCompletionSource reached, string table) : DbCommandInterceptor
    {
        private void Check(DbCommand command) { if (command.CommandText.Contains("FROM cerberus." + table + " ") && command.CommandText.Contains("FOR ")) reached.TrySetResult(); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default) { Check(c); return ValueTask.FromResult(r); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { Check(c); return ValueTask.FromResult(r); }
    }
    private sealed class BeforeTable(string table, Func<Task> action) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        private async Task Check(DbCommand c) { if (!Triggered && c.CommandText.Contains("FROM cerberus." + table + " ") && c.CommandText.Contains("FOR ")) { Triggered = true; await action(); } }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default) { await Check(c); return r; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { await Check(c); return r; }
    }
    private sealed class PauseAccount(TaskCompletionSource reached, TaskCompletionSource release) : DbCommandInterceptor
    {
        private bool used;
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken ct = default)
        { if (!used && c.CommandText.Contains("cerberus.account") && c.CommandText.Contains("FOR ")) { used = true; reached.TrySetResult(); await release.Task.WaitAsync(ct); } return r; }
    }
    private sealed class PauseContent(TaskCompletionSource reached, TaskCompletionSource release) : DbCommandInterceptor
    {
        private bool used;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default)
        { if (!used && (c.CommandText.Contains("UPDATE cerberus.folder AS r") || c.CommandText.Contains("UPDATE cerberus.record AS r"))) { used = true; reached.TrySetResult(); await release.Task.WaitAsync(ct); } return r; }
    }
    private sealed class ExpireAtInsert(DateTimeOffset deadline, bool after) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default)
        { if (!after && c.CommandText.Contains("INSERT INTO cerberus.collection(")) { Triggered = true; await Past(deadline); } return r; }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken ct = default)
        { if (after && r == 1 && c.CommandText.Contains("INSERT INTO cerberus.collection(")) { Triggered = true; await Past(deadline); } return r; }
    }
    private sealed class ThrowingFactory(Exception exception) : IDbContextFactory<AppDbContext>
    { public AppDbContext CreateDbContext() => throw exception; public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromException<AppDbContext>(exception); }
    private sealed class Fail(string stage) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int result, CancellationToken ct = default)
        {
            var sql = stage switch { "insert" => "INSERT INTO cerberus.collection(", "PC" => "INSERT INTO cerberus.profile_collection", "CF" => "INSERT INTO cerberus.collection_folder", "CR" => "INSERT INTO cerberus.collection_record", _ => "UPDATE cerberus." + stage + " SET" };
            if (!Triggered && result > 0 && c.CommandText.Contains(sql)) { Triggered = true; throw new TimeoutException("controlled failure after actual collection mutation"); } return ValueTask.FromResult(result);
        }
    }
}
