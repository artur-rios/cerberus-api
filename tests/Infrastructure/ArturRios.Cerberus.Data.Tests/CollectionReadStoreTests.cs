using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit.Abstractions;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class CollectionReadStoreTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenOwnedCollection_WhenGetting_ThenReturnOpaqueTargetAndOnlyCurrentDirectReferences(bool selected)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); using var secondKey = new ProtectionFixture(); using var outsideKey = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s);
        var second = await AssociationSetup.Items(fixture, s); var child = await Folder(s, a.Folder.Id);
        var inherited = await Record(s, child.Id);
        await using (var db = fixture.CreateContext())
            await db.Records.Where(x => x.Id == a.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.FolderId, child.Id));
        await Members(s, a.Collection, [a.Record.Id, second.Record.Id], [a.Folder.Id, second.Folder.Id]);
        var p = await Profile(s, owner, key, [a.Collection.PublicId]);
        var q = await Profile(s, owner, secondKey, [a.Collection.PublicId]);
        await Profile(s, owner, outsideKey, [second.Collection.PublicId]);
        var request = selected ? s with { Verifier = await Selected(s, p.Id) } : s;
        var before = await Snapshot(s); var capture = new CaptureRead();
        var result = await Store(capture).ReadAsync(new(request.Actor, request.Verifier, a.Collection.PublicId), default);
        Assert.Null(result.Error); var data = result.Data!;
        Assert.Equal(a.Collection.PublicId, data.Collection.CollectionId); Assert.Equal(a.Collection.Envelope, data.Collection.Envelope);
        Assert.Equal(a.Collection.Revision, data.Collection.Revision); Assert.Equal(a.Collection.ServerSequence, data.Collection.ServerSequence);
        Assert.Equal(a.Collection.EditedAt, data.Collection.EditedAt); Assert.Equal(1, data.Collection.KeyEpoch);
        Assert.Equal((selected ? new[] { p.PublicId } : new[] { p.PublicId, q.PublicId }).Order(), data.ProfileIds);
        Assert.Equal(new[] { a.Record.PublicId, second.Record.PublicId }.Order(), data.RecordIds);
        Assert.Equal(new[] { a.Folder.PublicId, second.Folder.PublicId }.Order(), data.FolderIds);
        Assert.DoesNotContain(inherited.PublicId, data.RecordIds); Assert.DoesNotContain(child.PublicId, data.FolderIds);
        Assert.Equal(before, await Snapshot(s)); Assert.Equal(1, capture.Readers); Assert.Equal(0, capture.Writes);
        Assert.DoesNotContain("FOR UPDATE", capture.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FOR SHARE", capture.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [FunctionalTheory]
    [InlineData(CollectionGrantAccess.ReadOnly, false)][InlineData(CollectionGrantAccess.ReadOnly, true)]
    [InlineData(CollectionGrantAccess.ReadWrite, false)][InlineData(CollectionGrantAccess.ReadWrite, true)]
    public async Task GivenNativeGrant_WhenGetting_ThenExposeOnlyRecipientProfilesAndDirectOwnerMembers(CollectionGrantAccess access, bool selected)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture(); using var secondKey = new ProtectionFixture();
        var foreign = await ProfileSetup.Create(fixture, owner); var s = await ProfileSetup.Create(fixture, recipient);
        var other = await ProfileSetup.Create(fixture, recipient); var a = await AssociationSetup.Items(fixture, foreign);
        await Members(foreign, a.Collection, [a.Record.Id], [a.Folder.Id]);
        await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, a.Collection, access);
        await AssociationSetup.Grant(fixture, foreign, owner, other, recipient, a.Collection, access);
        var ownerProfile = await Profile(foreign, owner, key, [a.Collection.PublicId]);
        var otherProfile = await Profile(other, recipient, key, [a.Collection.PublicId]);
        var p = await Profile(s, recipient, key, [a.Collection.PublicId]); var q = await Profile(s, recipient, secondKey, [a.Collection.PublicId]);
        var request = selected ? s with { Verifier = await Selected(s, p.Id) } : s;
        var before = await Snapshot(s); var ownerBefore = await Snapshot(foreign);
        var result = await Read(request, a.Collection.PublicId);
        Assert.Null(result.Error); Assert.Equal(a.Collection.Envelope, result.Data!.Collection.Envelope);
        Assert.Equal((selected ? new[] { p.PublicId } : new[] { p.PublicId, q.PublicId }).Order(), result.Data.ProfileIds);
        Assert.DoesNotContain(ownerProfile.PublicId, result.Data.ProfileIds); Assert.DoesNotContain(otherProfile.PublicId, result.Data.ProfileIds);
        Assert.Equal(new[] { a.Record.PublicId }, result.Data.RecordIds); Assert.Equal(new[] { a.Folder.PublicId }, result.Data.FolderIds);
        Assert.Equal(ownerBefore, await Snapshot(foreign));
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("record")][InlineData("folder")]
    public async Task GivenActualSelectedMemberAccessWithoutCollectionLink_WhenGettingCollection_Then404(string kind)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner);
        var a = await AssociationSetup.Items(fixture, s); await Members(s, a.Collection, [a.Record.Id], [a.Folder.Id]);
        var input = ProfileSetup.Input(s, owner, key) with { RecordIds = kind == "record" ? [a.Record.PublicId] : [], FolderIds = kind == "folder" ? [a.Folder.PublicId] : [] };
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor, s.Verifier, input), default)).Error);
        await using var db = fixture.CreateContext(); var p = await db.Profiles.SingleAsync(x => x.PublicId == input.ProfileId);
        var request = s with { Verifier = await Selected(s, p.Id) };
        if (kind == "record") Assert.Null((await new RecordReadStore(fixture).ReadAsync(new(s.Actor, request.Verifier, a.Record.PublicId), default)).Error);
        else Assert.Null((await new FolderReadStore(fixture).ReadAsync(new(s.Actor, request.Verifier, a.Folder.PublicId), default)).Error);
        var result = await Read(request, a.Collection.PublicId); Assert.Equal("not_found", result.Error); Assert.Null(result.Data);
    }

    [FunctionalTheory][InlineData("missing")][InlineData("foreign")][InlineData("unselectedOwned")][InlineData("unselectedNative")]
    public async Task GivenInaccessibleTarget_WhenGetting_Then404BeforeInspectingCorruptContent(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var foreign = await ProfileSetup.Create(fixture, owner); var s = await ProfileSetup.Create(fixture, recipient);
        var target = await Collection(kind == "unselectedOwned" ? s : foreign); var p = await Profile(s, recipient, key, []);
        if (kind == "unselectedNative")
        {
            var grant = await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, target);
            await using var db = fixture.CreateContext(); await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.RecipientKeyEnvelope, "bad"u8.ToArray()));
        }
        await using (var db = fixture.CreateContext()) await db.Collections.Where(x => x.Id == target.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.Envelope, "bad"u8.ToArray()).SetProperty(c => c.ServerSequence, 0));
        var result = await Read(kind.StartsWith("unselected", StringComparison.Ordinal) ? s with { Verifier = await Selected(s, p.Id) } : s, kind == "missing" ? Guid.NewGuid() : target.PublicId);
        Assert.Equal("not_found", result.Error); Assert.Null(result.Data);
    }

    [FunctionalTheory]
    [InlineData("recordTrash")][InlineData("recordTerminal")][InlineData("folderTrash")][InlineData("folderTerminal")]
    [InlineData("ancestorTrash")][InlineData("ancestorTerminal")][InlineData("incomplete")][InlineData("foreignAncestor")]
    [InlineData("foreignRecord")][InlineData("foreignFolder")][InlineData("profileTrash")][InlineData("profileTerminal")]
    public async Task GivenHiddenOrIncompleteDirectRelationships_WhenGetting_ThenOmitOnlyAffectedKinds(string kind)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner);
        var foreign = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s);
        var root = await Folder(s); var unrelated = await Folder(foreign); var p = await Profile(s, owner, key, [a.Collection.PublicId]);
        await Members(s, a.Collection, [a.Record.Id], [a.Folder.Id]);
        await using (var db = fixture.CreateContext())
        {
            await db.Records.Where(x => x.Id == a.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.FolderId, root.Id));
            await db.Folders.Where(x => x.Id == a.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.ParentFolderId, root.Id));
            if (kind == "recordTrash") await db.Records.Where(x => x.Id == a.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.DeletedAt, DateTimeOffset.UtcNow));
            if (kind == "folderTrash") await db.Folders.Where(x => x.Id == a.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.DeletedAt, DateTimeOffset.UtcNow));
            if (kind == "profileTrash") await db.Profiles.Where(x => x.Id == p.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow));
            if (kind == "ancestorTrash") await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.DeletedAt, DateTimeOffset.UtcNow));
            if (kind.EndsWith("Terminal", StringComparison.Ordinal))
            {
                db.TerminalErasures.Add(new() { ResourceKind = kind == "recordTerminal" ? "record" : kind == "profileTerminal" ? "profile" : "folder", ResourceId = kind == "recordTerminal" ? a.Record.PublicId : kind == "profileTerminal" ? p.PublicId : kind == "folderTerminal" ? a.Folder.PublicId : root.PublicId, DeletedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
            // Simulate persisted corruption that normal composite foreign keys prevent.
            await db.Database.OpenConnectionAsync(); await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
            if (kind is "incomplete" or "foreignAncestor") await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.ParentFolderId, kind == "incomplete" ? long.MaxValue : unrelated.Id));
            if (kind == "foreignRecord") await db.Records.Where(x => x.Id == a.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.AccountId, foreign.InternalId));
            if (kind == "foreignFolder") await db.Folders.Where(x => x.Id == a.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.AccountId, foreign.InternalId));
            await transaction.CommitAsync();
        }
        try
        {
            var result = await Read(s, a.Collection.PublicId); Assert.Null(result.Error);
            var both = kind is "ancestorTrash" or "ancestorTerminal" or "incomplete" or "foreignAncestor";
            Assert.Equal(both || kind is "recordTrash" or "recordTerminal" or "foreignRecord" ? [] : new[] { a.Record.PublicId }, result.Data!.RecordIds);
            Assert.Equal(both || kind is "folderTrash" or "folderTerminal" or "foreignFolder" ? [] : new[] { a.Folder.PublicId }, result.Data.FolderIds);
            Assert.Equal(kind is "profileTrash" or "profileTerminal" ? [] : new[] { p.PublicId }, result.Data.ProfileIds);
        }
        finally
        {
            // Restore FK consistency for other tests that dump and restore this shared fixture.
            await using var db = fixture.CreateContext();
            if (kind is "incomplete" or "foreignAncestor") await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.ParentFolderId, (long?)null));
            if (kind == "foreignRecord") await db.Records.Where(x => x.Id == a.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.AccountId, s.InternalId));
            if (kind == "foreignFolder") await db.Folders.Where(x => x.Id == a.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.AccountId, s.InternalId));
        }
    }

    [FunctionalTheory][InlineData("record", false)][InlineData("folder", false)][InlineData("record", true)][InlineData("folder", true)][InlineData("unrelated", false)]
    public async Task GivenCyclicAncestry_WhenGetting_Then503OnlyForRelevantFullyActiveMembers(string kind, bool hidden)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s);
        var root = await Folder(s); var child = await Folder(s, root.Id);
        await using (var db = fixture.CreateContext())
        {
            await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.ParentFolderId, child.Id).SetProperty(f => f.DeletedAt, hidden ? (DateTimeOffset?)DateTimeOffset.UtcNow : null));
            await db.Folders.Where(x => x.Id == a.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.ParentFolderId, child.Id));
            await db.Records.Where(x => x.Id == a.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.FolderId, child.Id));
        }
        await Members(s, a.Collection, kind == "record" ? [a.Record.Id] : [], kind == "folder" ? [a.Folder.Id] : []);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await Store().ReadAsync(new(s.Actor, s.Verifier, a.Collection.PublicId), ct.Token);
        if (!hidden && kind != "unrelated") { Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data); }
        else { Assert.Null(result.Error); Assert.Empty(result.Data!.RecordIds); Assert.Empty(result.Data.FolderIds); }
    }

    [FunctionalFact]
    public async Task GivenDamagedMemberCiphertextAndOrdering_WhenGetting_ThenReturnDirectIdentifiersWithoutParsingMembers()
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s);
        await Members(s, a.Collection, [a.Record.Id], [a.Folder.Id]);
        await using (var db = fixture.CreateContext())
        {
            await db.Records.Where(x => x.Id == a.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.Envelope, "bad"u8.ToArray()).SetProperty(r => r.ServerSequence, 0).SetProperty(r => r.Revision, -1).SetProperty(r => r.EditedAt, DateTimeOffset.MinValue));
            await db.Folders.Where(x => x.Id == a.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.Envelope, "bad"u8.ToArray()).SetProperty(f => f.ServerSequence, 0).SetProperty(f => f.Revision, -1).SetProperty(f => f.EditedAt, DateTimeOffset.MinValue));
        }
        var result = await Read(s, a.Collection.PublicId); Assert.Null(result.Error);
        Assert.Equal(new[] { a.Record.PublicId }, result.Data!.RecordIds); Assert.Equal(new[] { a.Folder.PublicId }, result.Data.FolderIds);
    }

    [FunctionalTheory][InlineData("account")][InlineData("profile")][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("grant")]
    public async Task GivenSamePublicIdInAllKinds_WhenGetting_ThenHonorTypedTerminalIdentity(string kind)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner);
        var id = Guid.NewGuid(); var a = await AssociationSetup.Items(fixture, s, id); await Members(s, a.Collection, [a.Record.Id], [a.Folder.Id]);
        var input = ProfileSetup.Input(s, owner, key); input = input with { ProfileId = id, KeyWrappers = input.KeyWrappers with { MasterKeyWrapper = owner.Wrap(s.AccountId, "profile", id, s.Actor) }, CollectionIds = [id] };
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor, s.Verifier, input), default)).Error);
        await using (var db = fixture.CreateContext()) { db.TerminalErasures.Add(new() { ResourceKind = kind, ResourceId = id, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        var result = await Read(s, id);
        if (kind == "collection") { Assert.Equal("not_found", result.Error); Assert.Null(result.Data); }
        else
        {
            Assert.Null(result.Error); Assert.Equal(id, result.Data!.Collection.CollectionId);
            Assert.Equal(kind == "profile" ? [] : new[] { id }, result.Data.ProfileIds);
            Assert.Equal(kind == "record" ? [] : new[] { id }, result.Data.RecordIds);
            Assert.Equal(kind == "folder" ? [] : new[] { id }, result.Data.FolderIds);
        }
    }

    [FunctionalTheory][InlineData(0L)][InlineData(-1L)][InlineData(9007199254740992L)]
    public async Task GivenInvalidTargetOrdering_WhenGetting_Then503(long sequence)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var target = await Collection(s);
        await using (var db = fixture.CreateContext()) await db.Collections.Where(x => x.Id == target.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.ServerSequence, sequence));
        var result = await Read(s, target.PublicId); Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data);
    }

    [FunctionalTheory][InlineData("revision")][InlineData("time")][InlineData("envelope")][InlineData("epoch")]
    public async Task GivenMalformedOwnedTargetProjection_WhenGetting_ThenForwardOnlyTargetForAuthoritativeQueryValidation(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var target = await Collection(s); var unrelated = await Collection(s);
        await using (var db = fixture.CreateContext())
        {
            await db.Collections.Where(x => x.Id == unrelated.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.ServerSequence, 0).SetProperty(c => c.Envelope, "bad"u8.ToArray()));
            if (kind == "revision") target.Revision = 0; if (kind == "time") target.EditedAt = DateTimeOffset.MinValue;
            if (kind == "envelope") target.Envelope = "bad"u8.ToArray(); if (kind == "epoch") target.KeyEpoch = 2;
            await db.Collections.Where(x => x.Id == target.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.Revision, target.Revision)
                .SetProperty(c => c.Envelope, target.Envelope).SetProperty(c => c.KeyEpoch, target.KeyEpoch));
            // Persist the actual default timestamp, not Npgsql's -infinity alias.
            if (kind == "time") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.collection SET edited_at=TIMESTAMPTZ '0001-01-01 00:00:00+00' WHERE id={target.Id}");
        }
        var result = await Read(s, target.PublicId); Assert.Null(result.Error); var row = result.Data!.Collection;
        Assert.Equal(target.PublicId, row.CollectionId); Assert.Equal(target.Envelope, row.Envelope); Assert.Equal(target.Revision, row.Revision);
        Assert.Equal(target.EditedAt, row.EditedAt); Assert.Equal(target.KeyEpoch, row.KeyEpoch);
    }

    [FunctionalTheory][InlineData("zero")][InlineData("unsafe")][InlineData("duplicate")]
    public async Task GivenUnrelatedCollectionCorruption_WhenGetting_ThenKeepValidTargetIndependent(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var target = await Collection(s); var other = await Collection(s);
        await using (var db = fixture.CreateContext()) await db.Collections.Where(x => x.Id == other.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.ServerSequence, kind == "zero" ? 0 : kind == "unsafe" ? ProtocolBinary.MaxInteger + 1 : target.ServerSequence).SetProperty(c => c.Envelope, "bad"u8.ToArray()));
        var result = await Read(s, target.PublicId); Assert.Null(result.Error); Assert.Equal(target.Envelope, result.Data!.Collection.Envelope);
    }

    [FunctionalTheory]
    [InlineData("missing", "not_found")][InlineData("closing", "not_found")][InlineData("terminal", "not_found")]
    [InlineData("access", "vault_access_denied")][InlineData("revoked", "vault_access_denied")][InlineData("expired", "vault_access_denied")]
    [InlineData("future", "vault_access_denied")][InlineData("policy", "vault_access_denied")][InlineData("generation", "vault_access_denied")]
    [InlineData("zeroPolicy", "vault_access_denied")][InlineData("zeroGeneration", "vault_access_denied")]
    [InlineData("nullExpiry", "vault_access_denied")][InlineData("disabledExpiry", "vault_access_denied")][InlineData("noExpiry", null)]
    public async Task GivenCurrentActorOrSessionLifecycle_WhenGetting_ThenRevalidateWithoutMutation(string kind, string? error)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var target = await Collection(s);
        await using (var db = fixture.CreateContext())
        {
            var a = await db.Accounts.SingleAsync(x => x.Id == s.InternalId); var v = await db.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == s.Verifier);
            if (kind == "closing") a.State = AccountState.ClosurePending;
            if (kind == "terminal") db.TerminalErasures.Add(new() { ResourceId = s.AccountId, ResourceKind = "account", DeletedAt = DateTimeOffset.UtcNow });
            if (kind == "revoked") v.Revoked = true; if (kind == "expired") v.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            if (kind == "future") v.IssuedAt = DateTimeOffset.UtcNow.AddMinutes(1); if (kind == "policy") v.PolicyRevision++; if (kind == "generation") v.RevocationGeneration++;
            if (kind == "zeroPolicy") v.PolicyRevision = 0; if (kind == "zeroGeneration") v.RevocationGeneration = 0;
            if (kind is "noExpiry" or "disabledExpiry") a.RenewalEnabled = false; if (kind is "noExpiry" or "nullExpiry") v.ExpiresAt = null;
            await db.SaveChangesAsync();
        }
        var before = await Snapshot(s); var result = await Read(s with { Actor = kind == "missing" ? Guid.NewGuid() : s.Actor, Verifier = kind == "access" ? new string('a', 64) : s.Verifier }, target.PublicId);
        Assert.Equal(error, result.Error); if (error is null) Assert.Equal(target.PublicId, result.Data!.Collection.CollectionId); else Assert.Null(result.Data);
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("valid", null)][InlineData("dangling", "vault_access_denied")][InlineData("foreign", "vault_access_denied")][InlineData("trash", "vault_access_denied")][InlineData("terminal", "vault_access_denied")]
    public async Task GivenSelectedProfileLifecycle_WhenGetting_ThenRequireCurrentActiveOwnedSelection(string kind, string? error)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var foreign = await ProfileSetup.Create(fixture, owner);
        var target = await Collection(s); var p = await Profile(kind == "foreign" ? foreign : s, owner, key, kind == "foreign" ? [] : [target.PublicId]);
        var verifier = await Selected(s, kind == "dangling" ? long.MaxValue : p.Id);
        await using (var db = fixture.CreateContext())
        {
            if (kind == "trash") await db.Profiles.Where(x => x.Id == p.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow));
            if (kind == "terminal") { db.TerminalErasures.Add(new() { ResourceKind = "profile", ResourceId = p.PublicId, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        }
        var result = await Read(s with { Verifier = verifier }, target.PublicId); Assert.Equal(error, result.Error);
        if (error is null) Assert.Equal(new[] { p.PublicId }, result.Data!.ProfileIds); else Assert.Null(result.Data);
    }

    [FunctionalTheory]
    [InlineData("revoked")][InlineData("wrongRecipient")][InlineData("unknownAccess")][InlineData("zeroRevision")][InlineData("unsafeRevision")]
    [InlineData("grantTerminal")][InlineData("collectionTrash")][InlineData("collectionTerminal")][InlineData("ownerClosing")][InlineData("ownerTerminal")]
    public async Task GivenHiddenNativeAuthority_WhenGetting_Then404BeforeNativeEvidence(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var foreign = await ProfileSetup.Create(fixture, owner); var s = await ProfileSetup.Create(fixture, recipient);
        var target = await Collection(foreign); var grant = await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, target);
        await using (var db = fixture.CreateContext())
        {
            var g = await db.CollectionGrants.SingleAsync(x => x.Id == grant.Id); g.RecipientKeyEnvelope = "bad"u8.ToArray();
            if (kind == "revoked") g.State = CollectionGrantState.Revoked; if (kind == "wrongRecipient") g.RecipientAccountId = foreign.InternalId;
            if (kind == "unknownAccess") g.Access = (CollectionGrantAccess)99; if (kind == "zeroRevision") g.Revision = 0; if (kind == "unsafeRevision") g.Revision = ProtocolBinary.MaxInteger + 1;
            if (kind == "collectionTrash") await db.Collections.Where(x => x.Id == target.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.DeletedAt, DateTimeOffset.UtcNow));
            if (kind == "ownerClosing") await db.Accounts.Where(x => x.Id == foreign.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
            if (kind.EndsWith("Terminal", StringComparison.Ordinal)) db.TerminalErasures.Add(new() { ResourceKind = kind == "grantTerminal" ? "grant" : kind == "ownerTerminal" ? "account" : "collection", ResourceId = kind == "grantTerminal" ? grant.PublicId : kind == "ownerTerminal" ? foreign.AccountId : target.PublicId, DeletedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var result = await Read(s, target.PublicId); Assert.Equal("not_found", result.Error); Assert.Null(result.Data);
    }

    [FunctionalTheory]
    [InlineData("blob")][InlineData("signature")][InlineData("owner")][InlineData("collection")][InlineData("grant")][InlineData("recipient")]
    [InlineData("revision")][InlineData("epoch")][InlineData("nativeEpoch")][InlineData("signedEpoch")][InlineData("signedRevision")]
    [InlineData("collectionEnvelope")][InlineData("ownerPins")][InlineData("recipientPins")][InlineData("missingOwnerPins")][InlineData("missingRecipientPins")]
    public async Task GivenInvalidNativeBinding_WhenGetting_Then503WithoutPartialContent(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var foreign = await ProfileSetup.Create(fixture, owner); var s = await ProfileSetup.Create(fixture, recipient);
        var target = await Collection(foreign); var grant = await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, target);
        await using (var db = fixture.CreateContext())
        {
            var g = await db.CollectionGrants.SingleAsync(x => x.Id == grant.Id);
            if (kind == "blob") g.RecipientKeyEnvelope = "bad"u8.ToArray();
            if (kind is "owner" or "collection" or "grant" or "recipient") g.RecipientKeyEnvelope = Bytes(owner.WrapGrant(kind == "owner" ? Guid.NewGuid() : foreign.AccountId, kind == "collection" ? Guid.NewGuid() : target.PublicId, kind == "grant" ? Guid.NewGuid() : grant.PublicId, kind == "recipient" ? Guid.NewGuid() : s.Actor, recipient.Material.RecipientKey));
            if (kind == "signature") { var wrapper = JsonSerializer.Deserialize<RecipientEnvelope>(g.RecipientKeyEnvelope, ProtectionFixture.Json)!; ProtocolBinary.TryDecode(wrapper.Signature, null, out var signature); signature[0] ^= 1; g.RecipientKeyEnvelope = Bytes(wrapper with { Signature = ProtocolBinary.Encode(signature) }); }
            if (kind == "revision") g.Revision = 2;
            if (kind == "signedEpoch") g.RecipientKeyEnvelope = Bytes(owner.WrapGrant(foreign.AccountId, target.PublicId, grant.PublicId, s.Actor, recipient.Material.RecipientKey, epoch: 2));
            if (kind == "signedRevision") g.RecipientKeyEnvelope = Bytes(owner.WrapGrant(foreign.AccountId, target.PublicId, grant.PublicId, s.Actor, recipient.Material.RecipientKey, revision: 2));
            if (kind == "epoch") await db.Collections.Where(x => x.Id == target.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.KeyEpoch, 2));
            if (kind == "nativeEpoch") await db.Collections.Where(x => x.Id == target.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.KeyEpoch, 2).SetProperty(c => c.Envelope, Bytes(ProfileSetup.Envelope(2))));
            if (kind == "collectionEnvelope") await db.Collections.Where(x => x.Id == target.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.Envelope, "bad"u8.ToArray()));
            if (kind is "ownerPins" or "recipientPins") await db.VaultProtections.Where(x => x.AccountId == (kind == "ownerPins" ? foreign.InternalId : s.InternalId)).ExecuteUpdateAsync(x => x.SetProperty(p => p.Material, "bad"u8.ToArray()));
            if (kind is "missingOwnerPins" or "missingRecipientPins") await db.VaultProtections.Where(x => x.AccountId == (kind == "missingOwnerPins" ? foreign.InternalId : s.InternalId)).ExecuteDeleteAsync();
            await db.SaveChangesAsync();
        }
        var before = await Snapshot(s); var ownerBefore = await Snapshot(foreign); var result = await Read(s, target.PublicId);
        Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s)); Assert.Equal(ownerBefore, await Snapshot(foreign));
    }

    [FunctionalTheory]
    [InlineData(true, "epoch")][InlineData(false, "epoch")][InlineData(true, "generation")][InlineData(false, "generation")]
    [InlineData(true, "zeroRevision")][InlineData(false, "zeroRevision")][InlineData(true, "unsafeRevision")][InlineData(false, "unsafeRevision")]
    [InlineData(true, "key")][InlineData(false, "key")]
    public async Task GivenStaleProtectionPins_WhenGetting_Then503(bool ownerPins, string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var replacement = new ProtectionFixture();
        var foreign = await ProfileSetup.Create(fixture, owner); var s = await ProfileSetup.Create(fixture, recipient); var target = await Collection(foreign);
        await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, target);
        await using (var db = fixture.CreateContext())
        {
            var pins = await db.VaultProtections.SingleAsync(x => x.AccountId == (ownerPins ? foreign.InternalId : s.InternalId));
            if (kind == "epoch") pins.KeyEpoch++; if (kind == "generation") pins.RecoveryGeneration++;
            if (kind == "zeroRevision") pins.Revision = 0; if (kind == "unsafeRevision") pins.Revision = ProtocolBinary.MaxInteger + 1;
            if (kind == "key") pins.Material = Bytes(replacement.Material); await db.SaveChangesAsync();
        }
        var result = await Read(s, target.PublicId); Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data);
    }

    [FunctionalTheory]
    [InlineData("collection", "numericString")][InlineData("collection", "unknown")][InlineData("collection", "duplicate")][InlineData("collection", "case")]
    [InlineData("wrapper", "numericString")][InlineData("wrapper", "unknown")][InlineData("wrapper", "duplicate")][InlineData("wrapper", "case")]
    [InlineData("owner", "numericString")][InlineData("owner", "unknown")][InlineData("owner", "duplicate")][InlineData("owner", "case")]
    [InlineData("recipient", "numericString")][InlineData("recipient", "unknown")][InlineData("recipient", "duplicate")][InlineData("recipient", "case")]
    public async Task GivenNoncanonicalNativeJson_WhenGetting_Then503(string targetKind, string shape)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var foreign = await ProfileSetup.Create(fixture, owner); var s = await ProfileSetup.Create(fixture, recipient);
        var target = await Collection(foreign); var grant = await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, target);
        await using (var db = fixture.CreateContext())
        {
            var pins = await db.VaultProtections.SingleAsync(x => x.AccountId == (targetKind == "owner" ? foreign.InternalId : s.InternalId));
            var source = Encoding.UTF8.GetString(targetKind == "collection" ? target.Envelope : targetKind == "wrapper" ? grant.RecipientKeyEnvelope : pins.Material);
            using var parsed = JsonDocument.Parse(source); var first = parsed.RootElement.EnumerateObject().First();
            var duplicate = JsonSerializer.Serialize(first.Name) + ":" + first.Value.GetRawText() + ",";
            var corrupt = shape switch { "numericString" => source.Replace("\"keyEpoch\":1", "\"keyEpoch\":\"1\"", StringComparison.Ordinal), "unknown" => source.Insert(1, "\"secret\":1,"), "duplicate" => source.Insert(1, duplicate), _ => source.Replace("\"format\"", "\"Format\"", StringComparison.Ordinal) };
            Assert.NotEqual(source, corrupt); var bytes = Encoding.UTF8.GetBytes(corrupt);
            if (targetKind == "collection") await db.Collections.Where(x => x.Id == target.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.Envelope, bytes));
            else if (targetKind == "wrapper") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.RecipientKeyEnvelope, bytes));
            else { pins.Material = bytes; await db.SaveChangesAsync(); }
        }
        var result = await Read(s, target.PublicId); Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data);
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenUnrelatedCorruptNativeGrant_WhenGettingTarget_ThenDoNotInspectOtherEvidence(bool sharedTarget)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var foreign = await ProfileSetup.Create(fixture, owner); var s = await ProfileSetup.Create(fixture, recipient);
        var target = await Collection(sharedTarget ? foreign : s); var other = await Collection(foreign);
        if (sharedTarget) await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, target);
        var bad = await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, other);
        await using (var db = fixture.CreateContext())
        {
            await db.CollectionGrants.Where(x => x.Id == bad.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.RecipientKeyEnvelope, "bad"u8.ToArray()));
            await db.Collections.Where(x => x.Id == other.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.ServerSequence, 0).SetProperty(c => c.Envelope, "bad"u8.ToArray()));
            if (!sharedTarget) await db.VaultProtections.Where(x => x.AccountId == foreign.InternalId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Material, "bad"u8.ToArray()));
        }
        var result = await Read(s, target.PublicId); Assert.Null(result.Error); Assert.Equal(target.PublicId, result.Data!.Collection.CollectionId);
    }

    [FunctionalTheory][InlineData("grant")][InlineData("profile")][InlineData("member")][InlineData("ownerPin")][InlineData("recipientPin")][InlineData("session")]
    public async Task GivenCurrentStateChangesAfterSql_WhenGetting_ThenKeepSnapshotAndNextGetObservesChange(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var foreign = await ProfileSetup.Create(fixture, owner); var s = await ProfileSetup.Create(fixture, recipient); var a = await AssociationSetup.Items(fixture, foreign);
        await Members(foreign, a.Collection, [a.Record.Id], [a.Folder.Id]); var grant = await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, a.Collection);
        var p = await Profile(s, recipient, key, [a.Collection.PublicId]); var request = s with { Verifier = await Selected(s, p.Id) };
        var interceptor = new ChangeAfterRead(async () =>
        {
            await using var db = fixture.CreateContext();
            if (kind == "grant") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked));
            if (kind == "profile") await db.ProfileCollections.Where(x => x.ProfileId == p.Id && x.CollectionId == a.Collection.Id).ExecuteDeleteAsync();
            if (kind == "member") { await db.CollectionRecords.Where(x => x.CollectionId == a.Collection.Id).ExecuteDeleteAsync(); await db.CollectionFolders.Where(x => x.CollectionId == a.Collection.Id).ExecuteDeleteAsync(); }
            if (kind is "ownerPin" or "recipientPin") await db.VaultProtections.Where(x => x.AccountId == (kind == "ownerPin" ? foreign.InternalId : s.InternalId)).ExecuteUpdateAsync(x => x.SetProperty(p => p.KeyEpoch, 2));
            if (kind == "session") await db.VaultAccessSessions.Where(x => x.HandleVerifier == request.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true));
        });
        var result = await Store(interceptor).ReadAsync(new(s.Actor, request.Verifier, a.Collection.PublicId), default);
        Assert.Null(result.Error); Assert.Equal(new[] { p.PublicId }, result.Data!.ProfileIds); Assert.Equal(new[] { a.Record.PublicId }, result.Data.RecordIds); Assert.Equal(new[] { a.Folder.PublicId }, result.Data.FolderIds);
        Assert.Equal(1, interceptor.Readers); var next = await Read(request, a.Collection.PublicId);
        if (kind == "member") { Assert.Null(next.Error); Assert.Empty(next.Data!.RecordIds); Assert.Empty(next.Data.FolderIds); }
        else { Assert.Equal(kind == "session" ? "vault_access_denied" : kind is "ownerPin" or "recipientPin" ? "persistence_unavailable" : "not_found", next.Error); Assert.Null(next.Data); }
    }

    [FunctionalTheory][InlineData(false, false)][InlineData(true, false)][InlineData(true, true)]
    public async Task GivenHundredUnrelatedDeepFoldersAndCollections_WhenExplainingGet_ThenBoundCandidatesToTargetDirectMembersAndAncestry(bool selected, bool shared)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, recipient); var foreign = await ProfileSetup.Create(fixture, owner); var targetOwner = shared ? foreign : s;
        var target = await Collection(targetOwner); var root = await Folder(targetOwner); var child = await Folder(targetOwner, root.Id); var record = await Record(targetOwner, child.Id);
        await Members(targetOwner, target, [record.Id], [child.Id]);
        if (shared) await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, target);
        var p = await Profile(s, recipient, key, [target.PublicId]);
        foreach (var account in new[] { s, foreign })
        {
            long? parent = null;
            for (var depth = 0; depth < 50; depth++) { parent = (await Folder(account, parent)).Id; await Collection(account); }
        }
        var capture = new CaptureRead(); var result = await Store(capture).ReadAsync(new(s.Actor, selected ? await Selected(s, p.Id) : s.Verifier, target.PublicId), default);
        Assert.Null(result.Error); Assert.Equal(1, capture.Readers);
        await using var db = fixture.CreateContext(); await db.Database.OpenConnectionAsync(); await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + capture.Sql; foreach (var parameter in capture.Parameters) command.Parameters.Add(parameter.Clone());
        using var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!); output.WriteLine(plan.RootElement.GetRawText());
        var nodes = Nodes(plan.RootElement[0].GetProperty("Plan")).ToArray();
        AssertRows("CTE target", 1); AssertRows("CTE record_candidates", 1); AssertRows("CTE folder_candidates", 1); AssertRows("CTE ancestry", 2);
        void AssertRows(string name, double expected) => Assert.Equal(expected, Assert.Single(nodes, n => n.TryGetProperty("Subplan Name", out var subplan) && subplan.GetString() == name).GetProperty("Actual Rows").GetDouble());
        static IEnumerable<JsonElement> Nodes(JsonElement node) { yield return node; if (node.TryGetProperty("Plans", out var children)) foreach (var childNode in children.EnumerateArray()) foreach (var nested in Nodes(childNode)) yield return nested; }
    }

    [FunctionalFact]
    public async Task GivenDatabaseExpiryBeforeStatement_WhenGetting_ThenDenyUsingDatabaseTimeWithoutMutation()
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var target = await Collection(s); var deadline = DateTimeOffset.UtcNow.AddSeconds(1);
        await using (var db = fixture.CreateContext()) await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ExpiresAt, deadline));
        var before = await Snapshot(s); var result = await Store(new ExpireBeforeRead(deadline)).ReadAsync(new(s.Actor, s.Verifier, target.PublicId), default);
        Assert.Equal("vault_access_denied", result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenInternalTimeoutOrCancellation_WhenGetting_Then503WithoutMutation(bool cancellation)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var target = await Collection(s); var before = await Snapshot(s);
        var result = await Store(new FailRead(cancellation)).ReadAsync(new(s.Actor, s.Verifier, target.PublicId), default);
        Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenCallerCancellation_WhenGetting_ThenPropagate(bool duringRead)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var target = await Collection(s); using var ct = new CancellationTokenSource();
        if (!duringRead) ct.Cancel(); var store = duringRead ? Store(new CancelRead(ct)) : Store();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(new(s.Actor, s.Verifier, target.PublicId), ct.Token));
    }

    [FunctionalFact]
    public async Task GivenUnavailableCollectionTable_WhenGetting_Then503WithoutMutation()
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var target = await Collection(s); var before = await Snapshot(s);
        await using var db = fixture.CreateContext(); await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection RENAME TO uc31_unavailable_collection");
        try { var result = await Read(s, target.PublicId); Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data); }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.uc31_unavailable_collection RENAME TO collection"); }
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("actor", "authentication_required")][InlineData("target", "validation_failed")]
    public async Task GivenInvalidTrustedRequest_WhenGetting_ThenRejectBeforePersistence(string kind, string error)
    {
        var result = await new CollectionReadStore(null!).ReadAsync(new(kind == "actor" ? Guid.Empty : Guid.NewGuid(), new string('a', 64), kind == "target" ? Guid.Empty : Guid.NewGuid()), default);
        Assert.Equal(error, result.Error); Assert.Null(result.Data);
    }

    private CollectionReadStore Store(IInterceptor? interceptor = null) => new(interceptor is null ? fixture : new ProfileSetup.Factory(fixture, interceptor));
    private Task<VaultResult<CollectionReadDetails>> Read(ProfileSetup.State state, Guid target) => Store().ReadAsync(new(state.Actor, state.Verifier, target), default);
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ProtectionFixture.Json);
    private async Task<VaultCollection> Collection(ProfileSetup.State state)
    {
        var row = new VaultCollection { PublicId = Guid.NewGuid(), AccountId = state.InternalId, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch };
        await using var db = fixture.CreateContext(); db.Collections.Add(row); await db.SaveChangesAsync(); return row;
    }
    private async Task<VaultFolder> Folder(ProfileSetup.State state, long? parent = null)
    {
        var row = new VaultFolder { PublicId = Guid.NewGuid(), AccountId = state.InternalId, ParentFolderId = parent, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch };
        await using var db = fixture.CreateContext(); db.Folders.Add(row); await db.SaveChangesAsync(); return row;
    }
    private async Task<VaultRecord> Record(ProfileSetup.State state, long? folder = null)
    {
        var row = new VaultRecord { PublicId = Guid.NewGuid(), AccountId = state.InternalId, FolderId = folder, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch };
        await using var db = fixture.CreateContext(); db.Records.Add(row); await db.SaveChangesAsync(); return row;
    }
    private async Task<Profile> Profile(ProfileSetup.State state, ProtectionFixture owner, ProtectionFixture scoped, Guid[] collections)
    {
        var input = ProfileSetup.Input(state, owner, scoped) with { CollectionIds = collections };
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(state.Actor, state.Verifier, input), default)).Error);
        await using var db = fixture.CreateContext(); return await db.Profiles.AsNoTracking().SingleAsync(x => x.PublicId == input.ProfileId);
    }
    private async Task Members(ProfileSetup.State state, VaultCollection collection, long[] records, long[] folders)
    {
        await using var db = fixture.CreateContext();
        db.CollectionRecords.AddRange(records.Select(id => new CollectionRecord { AccountId = state.InternalId, CollectionId = collection.Id, RecordId = id }));
        db.CollectionFolders.AddRange(folders.Select(id => new CollectionFolder { AccountId = state.InternalId, CollectionId = collection.Id, FolderId = id })); await db.SaveChangesAsync();
    }
    private async Task<string> Selected(ProfileSetup.State state, long profile)
    {
        var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); await using var db = fixture.CreateContext();
        db.VaultAccessSessions.Add(new() { AccountId = state.InternalId, ProfileId = profile, HandleVerifier = verifier, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 });
        await db.SaveChangesAsync(); return verifier;
    }
    private async Task<string> Snapshot(ProfileSetup.State state)
    {
        await using var db = fixture.CreateContext(); var id = state.InternalId;
        return JsonSerializer.Serialize(new
        {
            Account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == id), Protection = await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == id),
            Sessions = await db.VaultAccessSessions.AsNoTracking().Where(x => x.AccountId == id).OrderBy(x => x.Id).ToArrayAsync(),
            Collections = await db.Collections.AsNoTracking().Where(x => x.AccountId == id).OrderBy(x => x.Id).ToArrayAsync(),
            Records = await db.Records.AsNoTracking().Where(x => x.AccountId == id).OrderBy(x => x.Id).ToArrayAsync(),
            Folders = await db.Folders.AsNoTracking().Where(x => x.AccountId == id).OrderBy(x => x.Id).ToArrayAsync(),
            Profiles = await db.Profiles.AsNoTracking().Where(x => x.AccountId == id).OrderBy(x => x.Id).ToArrayAsync(),
            ProfileCollections = await db.ProfileCollections.AsNoTracking().Where(x => db.Profiles.Any(p => p.Id == x.ProfileId && p.AccountId == id)).OrderBy(x => x.ProfileId).ThenBy(x => x.CollectionId).ToArrayAsync(),
            ProfileRecords = await db.ProfileRecords.AsNoTracking().Where(x => x.AccountId == id).OrderBy(x => x.ProfileId).ThenBy(x => x.RecordId).ToArrayAsync(),
            ProfileFolders = await db.ProfileFolders.AsNoTracking().Where(x => x.AccountId == id).OrderBy(x => x.ProfileId).ThenBy(x => x.FolderId).ToArrayAsync(),
            CollectionRecords = await db.CollectionRecords.AsNoTracking().Where(x => x.AccountId == id).OrderBy(x => x.CollectionId).ThenBy(x => x.RecordId).ToArrayAsync(),
            CollectionFolders = await db.CollectionFolders.AsNoTracking().Where(x => x.AccountId == id).OrderBy(x => x.CollectionId).ThenBy(x => x.FolderId).ToArrayAsync(),
            Grants = await db.CollectionGrants.AsNoTracking().Where(x => x.RecipientAccountId == id || db.Collections.Any(c => c.Id == x.CollectionId && c.AccountId == id)).OrderBy(x => x.Id).ToArrayAsync()
        });
    }
    private sealed class CaptureRead : DbCommandInterceptor
    {
        public int Readers { get; private set; } public int Writes { get; private set; }
        public string Sql { get; private set; } = ""; public NpgsqlParameter[] Parameters { get; private set; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Readers++; Sql = command.CommandText; Parameters = command.Parameters.Cast<NpgsqlParameter>().Select(x => x.Clone()).ToArray(); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { Writes++; return ValueTask.FromResult(result); }
    }
    private sealed class ChangeAfterRead(Func<Task> change) : DbCommandInterceptor
    {
        public int Readers { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader reader, CancellationToken ct = default)
        { Readers++; if (Readers == 1) await change(); return reader; }
    }
    private sealed class ExpireBeforeRead(DateTimeOffset deadline) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { var delay = deadline - DateTimeOffset.UtcNow; if (delay > TimeSpan.Zero) await Task.Delay(delay + TimeSpan.FromMilliseconds(100), ct); return result; }
    }
    private sealed class FailRead(bool cancellation) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (cancellation) throw new OperationCanceledException(); throw new TimeoutException("private provider detail"); }
    }
    private sealed class CancelRead(CancellationTokenSource source) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { source.Cancel(); ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(result); }
    }
}
