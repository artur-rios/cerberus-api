using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Data.Records;
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
public class CollectionMembershipStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory]
    [InlineData("replace")][InlineData("empty")][InlineData("retain")][InlineData("promote")][InlineData("typed")][InlineData("sameEnvelope")]
    public async Task GivenOwnedMemberships_WhenReplacing_ThenOnlyTargetAndDirectSymmetricDifferenceAdvance(string mode)
    {
        using var owner = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s); var b = await AssociationSetup.Items(fixture, s, mode == "typed" ? Guid.NewGuid() : null);
        var child = await Folder(s, a.Folder.Id); var descendant = await Record(s, child.Id);
        await Members(s, a.Collection.Id, [a.Folder.Id], [a.Record.Id]); await Members(s, b.Collection.Id, [a.Folder.Id, b.Folder.Id], [a.Record.Id, b.Record.Id]);
        var input = Input(mode == "empty" ? [] : mode == "promote" ? [a.Folder.PublicId, child.PublicId] : mode is "retain" or "sameEnvelope" ? [a.Folder.PublicId] : [b.Folder.PublicId],
            mode == "empty" ? [] : mode == "promote" ? [a.Record.PublicId, descendant.PublicId] : mode is "retain" or "sameEnvelope" ? [a.Record.PublicId] : [b.Record.PublicId]);
        if (mode == "sameEnvelope") input = input with { Envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(a.Collection.Envelope, ProtectionFixture.Json)! };
        var before = await Snapshot(s); await Success(s, a.Collection, input, before, await Set(s, a.Collection, input));
    }

    [FunctionalTheory]
    [InlineData("PF")][InlineData("PR")][InlineData("CF")][InlineData("CR")][InlineData("targetFolder")][InlineData("targetRecord")][InlineData("descendant")][InlineData("typed")]
    public async Task GivenSelectedAlreadyEffectiveMember_WhenReplacing_ThenRetainOrPromoteWithoutClearingSelection(string route)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s, route == "typed" ? Guid.NewGuid() : null); var other = await AssociationSetup.Items(fixture, s);
        var child = await Folder(s, a.Folder.Id); var record = await Record(s, child.Id); var profile = await AssociationSetup.Profile(fixture, s, owner);
        await Routes(s, profile, a.Collection.Id, route == "PF" ? [a.Folder.Id] : [], route == "PR" ? [record.Id] : []);
        if (route is "CF" or "CR") await Routes(s, profile, other.Collection.Id, [], []);
        if (route is "CF" or "targetFolder" or "descendant" or "typed") await Members(s, route == "CF" ? other.Collection.Id : a.Collection.Id, [a.Folder.Id], route == "typed" ? [a.Record.Id] : []);
        if (route is "CR" or "targetRecord") await Members(s, route == "CR" ? other.Collection.Id : a.Collection.Id, [], [record.Id]);
        s = s with { Verifier = await Selected(s, profile) };
        var input = Input(route is "PR" or "CR" or "targetRecord" ? [] : [route == "descendant" ? child.PublicId : a.Folder.PublicId],
            route is "PR" or "CR" or "targetRecord" or "descendant" ? [record.PublicId] : route == "typed" ? [a.Record.PublicId] : []);
        var before = await Snapshot(s); await Success(s, a.Collection, input, before, await Set(s, a.Collection, input));
        await using var check = fixture.CreateContext(); Assert.Equal(profile, (await check.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == s.Verifier)).ProfileId);
    }

    [FunctionalTheory]
    [InlineData("privateFolder")][InlineData("privateRecord")][InlineData("PRContainer")][InlineData("CRContainer")][InlineData("parent")][InlineData("sibling")][InlineData("missingPC")][InlineData("privateCycle")]
    public async Task GivenSelectedScopeExpansion_WhenReplacing_Then404BeforeStaleRevisionOrPrivateCorruption(string mode)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s); var child = await Folder(s, a.Folder.Id); var sibling = await Folder(s, a.Folder.Id); var r = await Record(s, child.Id);
        var profile = await AssociationSetup.Profile(fixture, s, owner);
        if (mode != "missingPC") await Routes(s, profile, a.Collection.Id, mode is "parent" or "sibling" ? [child.Id] : [], mode == "PRContainer" ? [r.Id] : []);
        if (mode == "CRContainer") await Members(s, a.Collection.Id, [], [r.Id]);
        if (mode == "privateCycle") await Sql("UPDATE cerberus.folder SET parent_folder_id={0},revision=0 WHERE id={1}", child.Id, a.Folder.Id);
        s = s with { Verifier = await Selected(s, profile) };
        await Denied(s, a.Collection, Input(mode == "privateRecord" ? [] : [mode == "sibling" ? sibling.PublicId : a.Folder.PublicId], mode == "privateRecord" ? [r.PublicId] : []) with { ExpectedRevision = 77 }, "not_found");
    }

    [FunctionalTheory]
    [InlineData("folder", "missing")][InlineData("record", "missing")][InlineData("collection", "missing")]
    [InlineData("folder", "foreign")][InlineData("record", "foreign")][InlineData("collection", "foreign")]
    [InlineData("folder", "trash")][InlineData("record", "trash")][InlineData("collection", "trash")]
    [InlineData("folder", "terminal")][InlineData("record", "terminal")][InlineData("collection", "terminal")]
    public async Task GivenHiddenTypedTarget_WhenReplacing_Then404AndNoMutation(string kind, string state)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var foreign = await ProfileSetup.Create(fixture, owner);
        var a = await AssociationSetup.Items(fixture, s); var b = await AssociationSetup.Items(fixture, foreign);
        var id = state == "missing" ? Guid.NewGuid() : kind == "collection" ? (state == "foreign" ? b.Collection.PublicId : a.Collection.PublicId) : kind == "folder" ? (state == "foreign" ? b.Folder.PublicId : a.Folder.PublicId) : state == "foreign" ? b.Record.PublicId : a.Record.PublicId;
        if (state == "trash") await Sql("UPDATE cerberus." + kind + " SET deleted_at=statement_timestamp() WHERE public_id={0}", id);
        if (state == "terminal") await Terminal(kind, id);
        var target = kind == "collection" ? new VaultCollection { PublicId = id } : a.Collection;
        var other = await Snapshot(foreign);
        await Denied(s, target, Input(kind == "folder" ? [id] : [], kind == "record" ? [id] : []) with { ExpectedRevision = 77 }, "not_found"); Assert.Equal(other, await Snapshot(foreign));
    }

    [FunctionalTheory]
    [InlineData("cycle", false, "persistence_unavailable")][InlineData("cycle", true, "persistence_unavailable")]
    [InlineData("trash", false, "not_found")][InlineData("trash", true, "not_found")]
    [InlineData("terminal", false, "not_found")][InlineData("terminal", true, "not_found")]
    [InlineData("hiddenCycle", false, "not_found")][InlineData("hiddenCycle", true, "not_found")]
    [InlineData("missing", false, "not_found")][InlineData("missing", true, "not_found")]
    [InlineData("foreign", false, "not_found")][InlineData("foreign", true, "not_found")]
    public async Task GivenIncompleteOrCyclicAncestry_WhenReplacing_ThenFailClosed(string mode, bool record, string error)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s); var child = await Folder(s, a.Folder.Id); var r = await Record(s, child.Id);
        if (mode is "cycle" or "hiddenCycle") await Sql("UPDATE cerberus.folder SET parent_folder_id={0} WHERE id={1}", child.Id, a.Folder.Id);
        if (mode is "trash" or "hiddenCycle") await Sql("UPDATE cerberus.folder SET deleted_at=statement_timestamp() WHERE id={0}", a.Folder.Id);
        if (mode == "terminal") await Terminal("folder", a.Folder.PublicId);
        if (mode is "missing" or "foreign")
        {
            var foreign = await ProfileSetup.Create(fixture, owner); var f = await Folder(foreign);
            await using var db = fixture.CreateContext(); await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
            try { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.folder SET parent_folder_id={(mode == "missing" ? long.MaxValue : f.Id)} WHERE id={a.Folder.Id}"); }
            finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
        }
        await Denied(s, a.Collection, Input(record ? [] : [child.PublicId], record ? [r.PublicId] : []), error);
    }

    [FunctionalTheory][InlineData("unrelatedCycle")][InlineData("otherTerminalType")][InlineData("retainedExhausted")][InlineData("removedInactive")][InlineData("opaqueMember")]
    public async Task GivenIrrelevantEvidence_WhenReplacing_ThenOnlyRequiredCurrentEvidenceMatters(string mode)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s); var b = await AssociationSetup.Items(fixture, s);
        await Members(s, a.Collection.Id, [a.Folder.Id], [a.Record.Id]);
        if (mode == "unrelatedCycle") await Sql("UPDATE cerberus.folder SET parent_folder_id=id WHERE id={0}", b.Folder.Id);
        if (mode == "otherTerminalType") await Terminal("record", a.Folder.PublicId);
        if (mode == "retainedExhausted") await Sql("UPDATE cerberus.folder SET revision=9007199254740991 WHERE id={0}", a.Folder.Id);
        if (mode == "removedInactive") await Sql("UPDATE cerberus.folder SET deleted_at=statement_timestamp(),revision=0 WHERE id={0}", a.Folder.Id);
        if (mode == "opaqueMember") await Sql("UPDATE cerberus.folder SET envelope='bad'::bytea WHERE id={0}", b.Folder.Id);
        var input = Input(mode == "removedInactive" ? [] : mode == "opaqueMember" ? [b.Folder.PublicId] : [a.Folder.PublicId], [a.Record.PublicId]);
        var before = await Snapshot(s); await Success(s, a.Collection, input, before, await Set(s, a.Collection, input));
    }

    [FunctionalTheory]
    [InlineData("collection", "revision")][InlineData("collection", "sequence")][InlineData("collection", "unsafe")][InlineData("collection", "time")][InlineData("collection", "purge")][InlineData("collection", "max")]
    [InlineData("folder", "revision")][InlineData("folder", "sequence")][InlineData("folder", "unsafe")][InlineData("folder", "time")][InlineData("folder", "purge")][InlineData("folder", "max")]
    [InlineData("record", "revision")][InlineData("record", "sequence")][InlineData("record", "unsafe")][InlineData("record", "time")][InlineData("record", "purge")][InlineData("record", "max")]
    public async Task GivenRequiredStructuralCorruptionOrExhaustion_WhenReplacing_ThenRollback(string kind, string field)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s);
        var expression = field switch { "sequence" => "server_sequence=0", "unsafe" => "server_sequence=9007199254740992", "time" => "edited_at='infinity'", "purge" => "purge_at=statement_timestamp()", "max" => "revision=9007199254740991", _ => "revision=0" };
        await Sql("UPDATE cerberus." + kind + " SET " + expression + " WHERE id={0}", kind == "collection" ? a.Collection.Id : kind == "folder" ? a.Folder.Id : a.Record.Id);
        await Denied(s, a.Collection, Input([a.Folder.PublicId], [a.Record.PublicId]), field == "max" ? "revision_conflict" : "persistence_unavailable");
    }

    [FunctionalTheory]
    [InlineData("stale", "revision_conflict")][InlineData("bad", "persistence_unavailable")][InlineData("duplicate", "persistence_unavailable")][InlineData("unknown", "persistence_unavailable")][InlineData("case", "persistence_unavailable")][InlineData("number", "persistence_unavailable")][InlineData("storedEpoch", "persistence_unavailable")]
    [InlineData("epoch", "validation_failed")][InlineData("nonce", "validation_failed")][InlineData("salt", "validation_failed")]
    public async Task GivenInvalidEnvelopeOrRevision_WhenReplacing_ThenNoMutation(string mode, string error)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s); var old = JsonSerializer.Deserialize<EncryptedEnvelope>(a.Collection.Envelope, ProtectionFixture.Json)!; var input = Input();
        if (mode == "stale") input = input with { ExpectedRevision = 9 };
        if (mode is "epoch" or "nonce" or "salt") input = input with { Envelope = mode == "epoch" ? input.Envelope with { KeyEpoch = 2 } : mode == "nonce" ? input.Envelope with { Nonce = old.Nonce } : input.Envelope with { KeySalt = old.KeySalt } };
        if (mode == "storedEpoch") await Sql("UPDATE cerberus.collection SET key_epoch=2 WHERE id={0}", a.Collection.Id);
        if (mode is "bad" or "duplicate" or "unknown" or "case" or "number")
        {
            var text = Encoding.UTF8.GetString(a.Collection.Envelope); var bad = mode switch { "duplicate" => text.Replace("{", "{\"keyEpoch\":1,"), "unknown" => text.Replace("{", "{\"secret\":1,"), "case" => text.Replace("\"format\"", "\"Format\""), "number" => text.Replace("\"keyEpoch\":1", "\"keyEpoch\":\"1\""), _ => "bad" };
            await Sql("UPDATE cerberus.collection SET envelope={0} WHERE id={1}", Encoding.UTF8.GetBytes(bad), a.Collection.Id);
        }
        await Denied(s, a.Collection, input, error);
    }

    [FunctionalTheory]
    [InlineData("closing", "not_found")][InlineData("terminal", "not_found")][InlineData("revoked", "vault_access_denied")][InlineData("expired", "vault_access_denied")][InlineData("future", "vault_access_denied")][InlineData("policy", "vault_access_denied")][InlineData("generation", "vault_access_denied")][InlineData("zeroPolicy", "vault_access_denied")][InlineData("nullExpiry", "vault_access_denied")][InlineData("disabledExpiry", "vault_access_denied")][InlineData("selection", "vault_access_denied")]
    public async Task GivenLostCurrentAuthority_WhenReplacing_ThenDenialPrecedesStaleRevision(string mode, string error)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s);
        await using (var db = fixture.CreateContext())
        {
            var account = await db.Accounts.SingleAsync(x => x.Id == s.InternalId); var session = await db.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == s.Verifier);
            if (mode == "closing") account.State = AccountState.ClosurePending;
            if (mode == "terminal") db.TerminalErasures.Add(new() { ResourceKind = "account", ResourceId = s.AccountId, DeletedAt = DateTimeOffset.UtcNow });
            if (mode == "revoked") session.Revoked = true; if (mode == "expired") session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); if (mode == "future") session.IssuedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            if (mode == "policy") session.PolicyRevision++; if (mode == "generation") session.RevocationGeneration++; if (mode == "zeroPolicy") session.PolicyRevision = 0;
            if (mode == "nullExpiry") session.ExpiresAt = null; if (mode == "disabledExpiry") account.RenewalEnabled = false; if (mode == "selection") session.ProfileId = long.MaxValue;
            await db.SaveChangesAsync();
        }
        await Denied(s, a.Collection, Input() with { ExpectedRevision = 77 }, error);
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenNativeRecipientsAndOverlappingCollection_WhenReplacing_ThenCurrentReadsChangeAndEveryGrantStaysExact(bool selected)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); using var second = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, owner); var recipient = await ProfileSetup.Create(fixture, client); var recipient2 = await ProfileSetup.Create(fixture, second);
        var a = await AssociationSetup.Items(fixture, s); var b = await AssociationSetup.Items(fixture, s); var child = await Record(s, b.Folder.Id);
        await Members(s, a.Collection.Id, [a.Folder.Id], [a.Record.Id]); await Members(s, b.Collection.Id, [], [a.Record.Id]);
        await AssociationSetup.Grant(fixture, s, owner, recipient, client, a.Collection); await AssociationSetup.Grant(fixture, s, owner, recipient2, second, a.Collection, CollectionGrantAccess.ReadWrite);
        await AssociationSetup.Grant(fixture, s, owner, recipient, client, b.Collection);
        if (selected) { var p = await AssociationSetup.Profile(fixture, s, owner); await Routes(s, p, a.Collection.Id, [b.Folder.Id], [b.Record.Id]); s = s with { Verifier = await Selected(s, p) }; }
        var input = Input([b.Folder.PublicId], [b.Record.PublicId]); var before = await Snapshot(s); var r1 = await Snapshot(recipient); var r2 = await Snapshot(recipient2);
        await Success(s, a.Collection, input, before, await Set(s, a.Collection, input));
        foreach (var r in new[] { recipient, recipient2 })
        {
            Assert.Null((await new FolderReadStore(fixture).ReadAsync(new(r.Actor, r.Verifier, b.Folder.PublicId), default)).Error);
            Assert.Null((await new RecordReadStore(fixture).ReadAsync(new(r.Actor, r.Verifier, child.PublicId), default)).Error);
            Assert.Equal("not_found", (await new FolderReadStore(fixture).ReadAsync(new(r.Actor, r.Verifier, a.Folder.PublicId), default)).Error);
        }
        Assert.Null((await new RecordReadStore(fixture).ReadAsync(new(recipient.Actor, recipient.Verifier, a.Record.PublicId), default)).Error);
        Assert.Equal("not_found", (await new RecordReadStore(fixture).ReadAsync(new(recipient2.Actor, recipient2.Verifier, a.Record.PublicId), default)).Error);
        foreach (var r in new[] { recipient, recipient2 })
        {
            var folders = await new FolderListStore(fixture).ListAsync(new(r.Actor, r.Verifier, 100, 0, null), default);
            Assert.Null(folders.Error); Assert.Equal(new[] { b.Folder.PublicId }, folders.Data!.Items.Select(x => x.FolderId));
            var records = await new RecordListStore(fixture).ListAsync(new(r.Actor, r.Verifier, 100, 0, null), default);
            Assert.Null(records.Error); Assert.Equal((r == recipient ? new[] { a.Record.PublicId, b.Record.PublicId, child.PublicId } : new[] { b.Record.PublicId, child.PublicId }).Order(), records.Data!.Items.Select(x => x.RecordId).Order());
        }
        Assert.Equal(r1, await Snapshot(recipient)); Assert.Equal(r2, await Snapshot(recipient2));
    }

    [FunctionalTheory]
    [InlineData("wrapper")][InlineData("revision")][InlineData("ownerPins")][InlineData("recipientPins")][InlineData("missingOwner")][InlineData("missingRecipient")][InlineData("ownerRevision")][InlineData("recipientEpoch")][InlineData("recipientGeneration")][InlineData("identity")][InlineData("access")]
    public async Task GivenMalformedSecondActiveGrant_WhenReplacing_ThenValidateAllRequiredNativeEvidence(string mode)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); using var second = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var r2 = await ProfileSetup.Create(fixture, second); var a = await AssociationSetup.Items(fixture, s);
        await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection); var bad = await AssociationSetup.Grant(fixture, s, owner, r2, second, a.Collection);
        if (mode == "wrapper") await Sql("UPDATE cerberus.collection_grant SET recipient_key_envelope='bad'::bytea WHERE id={0}", bad.Id);
        if (mode == "revision") await Sql("UPDATE cerberus.collection_grant SET revision=2 WHERE id={0}", bad.Id);
        if (mode == "access") await Sql("UPDATE cerberus.collection_grant SET access=99 WHERE id={0}", bad.Id);
        if (mode is "ownerPins" or "recipientPins") await Sql("UPDATE cerberus.vault_protection SET material='bad'::bytea WHERE account_id={0}", mode == "ownerPins" ? s.InternalId : r2.InternalId);
        if (mode is "missingOwner" or "missingRecipient") await Sql("DELETE FROM cerberus.vault_protection WHERE account_id={0}", mode == "missingOwner" ? s.InternalId : r2.InternalId);
        if (mode == "ownerRevision") await Sql("UPDATE cerberus.vault_protection SET revision=0 WHERE account_id={0}", s.InternalId);
        if (mode == "recipientEpoch") await Sql("UPDATE cerberus.vault_protection SET key_epoch=2 WHERE account_id={0}", r2.InternalId);
        if (mode == "recipientGeneration") await Sql("UPDATE cerberus.vault_protection SET recovery_generation=2 WHERE account_id={0}", r2.InternalId);
        if (mode == "identity") await Sql("UPDATE cerberus.collection_grant SET recipient_key_envelope={0} WHERE id={1}", Bytes(owner.WrapGrant(s.AccountId, a.Collection.PublicId, bad.PublicId, Guid.NewGuid(), second.Material.RecipientKey)), bad.Id);
        await Denied(s, a.Collection, Input([a.Folder.PublicId], [a.Record.PublicId]), "persistence_unavailable");
    }

    [FunctionalTheory][InlineData("revoked")][InlineData("unrelated")][InlineData("terminal")]
    public async Task GivenIrrelevantMalformedGrant_WhenReplacing_ThenDoNotInspectOrChangeIt(string mode)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s); var b = await AssociationSetup.Items(fixture, s);
        var grant = await AssociationSetup.Grant(fixture, s, owner, r, client, mode == "unrelated" ? b.Collection : a.Collection);
        await Sql("UPDATE cerberus.collection_grant SET recipient_key_envelope='bad'::bytea WHERE id={0}", grant.Id);
        if (mode == "revoked") await Sql("UPDATE cerberus.collection_grant SET state={0} WHERE id={1}", (int)CollectionGrantState.Revoked, grant.Id);
        if (mode == "terminal") await Terminal("grant", grant.PublicId);
        var input = Input([a.Folder.PublicId]); var before = await Snapshot(s); await Success(s, a.Collection, input, before, await Set(s, a.Collection, input));
    }

    [FunctionalTheory][InlineData(false, false)][InlineData(true, false)][InlineData(false, true)][InlineData(true, true)]
    public async Task GivenVisibleForeignCollection_WhenReplacing_ThenNativeValidated403RegardlessOfAccess(bool write, bool malformed)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var grant = await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection, write ? CollectionGrantAccess.ReadWrite : CollectionGrantAccess.ReadOnly);
        if (malformed) await Sql("UPDATE cerberus.collection_grant SET recipient_key_envelope='bad'::bytea WHERE id={0}", grant.Id);
        var before = await Snapshot(s); await Denied(r, a.Collection, Input() with { ExpectedRevision = 77 }, malformed ? "persistence_unavailable" : "vault_access_denied"); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("target")][InlineData("deleteFolder")][InlineData("deleteRecord")][InlineData("insertFolder")][InlineData("insertRecord")][InlineData("folder1")][InlineData("folder2")][InlineData("record1")][InlineData("record2")]
    public async Task GivenFaultAfterActualMutation_WhenReplacing_ThenEntireGraphRollsBackAndCleanRetrySucceeds(string stage)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s); var b = await AssociationSetup.Items(fixture, s); await Members(s, a.Collection.Id, [a.Folder.Id], [a.Record.Id]);
        var input = Input([b.Folder.PublicId], [b.Record.PublicId]); var before = await Snapshot(s); var hook = new Fault(stage);
        Assert.Equal("persistence_unavailable", (await new CollectionMembershipStore(new ProfileSetup.Factory(fixture, hook)).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, input), default)).Error);
        Assert.True(hook.Triggered); Assert.Equal(before, await Snapshot(s)); await Success(s, a.Collection, input, before, await Set(s, a.Collection, input));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenExhaustedOrRegressedSequence_WhenReplacing_ThenAtomicFailure(bool exhausted)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s); await using var db = fixture.CreateContext();
        var old = await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();
        try { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{(exhausted ? ProtocolBinary.MaxInteger : 1)},true)"); await Denied(s, a.Collection, Input([a.Folder.PublicId]), exhausted ? "revision_conflict" : "persistence_unavailable"); }
        finally { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{old},true)"); }
    }

    [FunctionalTheory][InlineData("cancel")][InlineData("timeout")][InlineData("db")][InlineData("update")][InlineData("json")][InlineData("format")][InlineData("wrapped")]
    public async Task GivenInternalProviderFailure_WhenReplacing_Then503(string kind)
    {
        Exception failure = kind switch { "cancel" => new OperationCanceledException("private"), "timeout" => new TimeoutException("private"), "db" => new NpgsqlException("private"), "update" => new DbUpdateException("private"), "json" => new JsonException("private"), "format" => new FormatException("private"), _ => new InvalidOperationException("private", new TimeoutException("private")) };
        Assert.Equal("persistence_unavailable", (await new CollectionMembershipStore(new ThrowingFactory(failure)).SetAsync(new(Guid.NewGuid(), "v", Guid.NewGuid(), Input()), default)).Error);
    }

    [FunctionalFact]
    public async Task GivenUnavailableDatabaseOrCallerCancellation_WhenReplacing_Then503OrCancellationPropagation()
    {
        var request = new CollectionMembershipRequest(Guid.NewGuid(), "v", Guid.NewGuid(), Input());
        var dead = new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=absent;Username=none;Timeout=1;Pooling=false").Options);
        Assert.Equal("persistence_unavailable", (await new CollectionMembershipStore(dead).SetAsync(request, default)).Error);
        using var ct = new CancellationTokenSource(); ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMembershipStore(fixture).SetAsync(request, ct.Token));
    }

    [FunctionalFact]
    public async Task GivenConcurrentSameRevisionAndLostResponseRetry_WhenReplacing_ThenExactlyOneWinner()
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s);
        var input = Input([a.Folder.PublicId], [a.Record.PublicId]); var results = await Task.WhenAll(Set(s, a.Collection, input), Set(s, a.Collection, input));
        Assert.Single(results, x => x.Error is null); Assert.Single(results, x => x.Error == "revision_conflict");
        await Denied(s, a.Collection, input, "revision_conflict");
    }

    [FunctionalTheory]
    [InlineData("account")][InlineData("vault_access_session")][InlineData("profile")][InlineData("collection")][InlineData("collection_grant")][InlineData("folder")][InlineData("record")][InlineData("collection_folder")][InlineData("collection_record")]
    public async Task GivenAuthorityExpiresWhileWaitingAtEachLockPhase_WhenReplacing_ThenCurrent403AndNoMutation(string table)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var recipient = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        await Members(s, a.Collection.Id, [a.Folder.Id], [a.Record.Id]); var g = await AssociationSetup.Grant(fixture, s, owner, recipient, client, a.Collection);
        var p = await AssociationSetup.Profile(fixture, s, owner); await Routes(s, p, a.Collection.Id, [], []); s = s with { Verifier = await Selected(s, p) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1.5); await Sql("UPDATE cerberus.vault_access_session SET expires_at={0} WHERE handle_verifier={1}", deadline, s.Verifier); var before = await Snapshot(s);
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        var predicate = table switch { "account" => "id={0}", "vault_access_session" => "handle_verifier={0}", "collection_folder" or "collection_record" => "collection_id={0}", _ => "id={0}" };
        object id = table switch { "account" => s.InternalId, "vault_access_session" => s.Verifier, "profile" => p, "folder" => a.Folder.Id, "record" => a.Record.Id, "collection_grant" => g.Id, _ => a.Collection.Id };
        var lockSql = "SELECT FROM cerberus." + table + " WHERE " + predicate + " FOR UPDATE";
        await blocker.Database.ExecuteSqlRawAsync(lockSql, id);
        var reached = SignalSource(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var task = new CollectionMembershipStore(new ProfileSetup.Factory(fixture, new Signal(table, reached))).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, Input([a.Folder.PublicId], [a.Record.PublicId]) with { ExpectedRevision = 77 }), ct.Token);
        try { await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(task.IsCompleted); await Past(deadline); } finally { await tx.CommitAsync(); }
        Assert.Equal("vault_access_denied", (await task).Error); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("collectionUpdate", false)][InlineData("collectionUpdate", true)][InlineData("collectionTrash", false)][InlineData("collectionTrash", true)]
    [InlineData("association", false)][InlineData("association", true)][InlineData("recordMove", false)][InlineData("recordMove", true)][InlineData("folderMove", false)][InlineData("folderMove", true)]
    [InlineData("recordTrash", false)][InlineData("recordTrash", true)][InlineData("folderTrash", false)][InlineData("folderTrash", true)][InlineData("recordUpdate", false)][InlineData("recordUpdate", true)][InlineData("folderUpdate", false)][InlineData("folderUpdate", true)]
    public async Task GivenActualOwnerWriter_WhenRacingMembership_ThenSerializeAndPreserveWinner(string writer, bool membershipFirst)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s); var destination = await Folder(s); var p = await AssociationSetup.Profile(fixture, s, owner);
        var input = Input([a.Folder.PublicId], [a.Record.PublicId]); var reached = SignalSource(); var release = SignalSource(); var waiting = SignalSource(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<string?> Write(IDbContextFactory<AppDbContext> f) => writer switch
        {
            "collectionUpdate" => (await new CollectionUpdateStore(f).UpdateAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, new(1, ProfileSetup.Envelope(), DateTimeOffset.UnixEpoch)), ct.Token)).Error,
            "collectionTrash" => (await new CollectionTrashStore(f).TrashAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, 1), ct.Token)).Error,
            "association" => (await new ProfileAssociationStore(f).SetAsync(new(s.Actor, s.Verifier, (await ProfileRow(p)).PublicId, new(1, [], [], [a.Collection.PublicId])), ct.Token)).Error,
            "recordMove" => (await new RecordMoveStore(f).MoveAsync(new(s.Actor, s.Verifier, a.Record.PublicId, 1, destination.PublicId), ct.Token)).Error,
            "folderMove" => (await new FolderMoveStore(f).MoveAsync(new(s.Actor, s.Verifier, a.Folder.PublicId, 1, destination.PublicId), ct.Token)).Error,
            "recordTrash" => (await new RecordTrashStore(f).TrashAsync(new(s.Actor, s.Verifier, a.Record.PublicId, 1), ct.Token)).Error,
            "folderTrash" => (await new FolderTrashStore(f).TrashAsync(new(s.Actor, s.Verifier, a.Folder.PublicId, 1), ct.Token)).Error,
            "recordUpdate" => (await new RecordUpdateStore(f).UpdateAsync(new(s.Actor, s.Verifier, a.Record.PublicId, new(1, ProfileSetup.Envelope(), DateTimeOffset.UnixEpoch)), ct.Token)).Error,
            _ => (await new FolderUpdateStore(f).UpdateAsync(new(s.Actor, s.Verifier, a.Folder.PublicId, new(1, ProfileSetup.Envelope(), DateTimeOffset.UnixEpoch)), ct.Token)).Error
        };
        Task<VaultResult<CollectionMembershipDetails>> membership; Task<string?> write;
        if (membershipFirst)
        {
            membership = new CollectionMembershipStore(new ProfileSetup.Factory(fixture, new Hook("UPDATE cerberus.collection AS", async _ => { reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }))).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, input), ct.Token);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); write = Write(new ProfileSetup.Factory(fixture, new Signal("account", waiting)));
        }
        else
        {
            write = Write(new ProfileSetup.Factory(fixture, new PauseAccount(reached, release))); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            membership = new CollectionMembershipStore(new ProfileSetup.Factory(fixture, new Signal("account", waiting))).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, input), ct.Token);
        }
        try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(membershipFirst ? write.IsCompleted : membership.IsCompleted); } finally { release.TrySetResult(); }
        await Task.WhenAll(membership, write);
        Assert.Equal(membershipFirst && writer != "association" ? "revision_conflict" : null, write.Result);
        Assert.Equal(!membershipFirst && writer == "collectionUpdate" ? "revision_conflict" : !membershipFirst && writer is "collectionTrash" or "recordTrash" or "folderTrash" ? "not_found" : null, membership.Result.Error);
        await using var check = fixture.CreateContext();
        if (membership.Result.Error is null) { Assert.Equal(2, (await check.Collections.SingleAsync(x => x.Id == a.Collection.Id)).Revision); Assert.True(await check.CollectionFolders.AnyAsync(x => x.CollectionId == a.Collection.Id && x.FolderId == a.Folder.Id)); Assert.True(await check.CollectionRecords.AnyAsync(x => x.CollectionId == a.Collection.Id && x.RecordId == a.Record.Id)); }
        if (writer == "recordMove") Assert.Equal(membershipFirst ? null : destination.Id, (await check.Records.SingleAsync(x => x.Id == a.Record.Id)).FolderId);
        if (writer == "folderMove") Assert.Equal(membershipFirst ? null : destination.Id, (await check.Folders.SingleAsync(x => x.Id == a.Folder.Id)).ParentFolderId);
    }

    [FunctionalTheory][InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task GivenNativeRecipientWriter_WhenRacingRemoval_ThenCollectionBeforeContentLocksPreserveWinner(bool record, bool membershipFirst)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        await Members(s, a.Collection.Id, [a.Folder.Id], [a.Record.Id]); await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection, CollectionGrantAccess.ReadWrite);
        var reached = SignalSource(); var release = SignalSource(); var waiting = SignalSource(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var replacement = ProfileSetup.Envelope();
        async Task<string?> Write(IDbContextFactory<AppDbContext> f) => record ? (await new RecordUpdateStore(f).UpdateAsync(new(r.Actor, r.Verifier, a.Record.PublicId, new(1, replacement, DateTimeOffset.UnixEpoch)), ct.Token)).Error : (await new FolderUpdateStore(f).UpdateAsync(new(r.Actor, r.Verifier, a.Folder.PublicId, new(1, replacement, DateTimeOffset.UnixEpoch)), ct.Token)).Error;
        Task<VaultResult<CollectionMembershipDetails>> membership; Task<string?> write;
        if (membershipFirst)
        {
            membership = new CollectionMembershipStore(new ProfileSetup.Factory(fixture, new Hook("UPDATE cerberus.collection AS", async _ => { reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }))).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, Input()), ct.Token);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); write = Write(new ProfileSetup.Factory(fixture, new Signal("collection", waiting)));
        }
        else
        {
            write = Write(new ProfileSetup.Factory(fixture, new Hook(record ? "UPDATE cerberus.record AS" : "UPDATE cerberus.folder AS", async _ => { reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }))); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            membership = new CollectionMembershipStore(new ProfileSetup.Factory(fixture, new Signal("collection", waiting))).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, Input()), ct.Token);
        }
        try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(membershipFirst ? write.IsCompleted : membership.IsCompleted); } finally { release.TrySetResult(); }
        await Task.WhenAll(membership, write); Assert.Equal(membershipFirst ? "not_found" : null, write.Result); Assert.Equal(membershipFirst ? null : "revision_conflict", membership.Result.Error);
        await using var check = fixture.CreateContext(); Assert.Equal(membershipFirst ? 0 : 1, await check.CollectionRecords.CountAsync(x => x.CollectionId == a.Collection.Id));
        Assert.Equal(membershipFirst ? record ? a.Record.Envelope : a.Folder.Envelope : Bytes(replacement), record ? (await check.Records.SingleAsync(x => x.Id == a.Record.Id)).Envelope : (await check.Folders.SingleAsync(x => x.Id == a.Folder.Id)).Envelope);
    }

    [FunctionalTheory]
    [InlineData("newFolder")][InlineData("newRecord")][InlineData("newGrant")][InlineData("grantPin")][InlineData("grantWrapper")][InlineData("parent")][InlineData("PC")][InlineData("revision")][InlineData("corruption")]
    public async Task GivenGraphOrNativeEvidenceChangesAtFinalWrite_WhenReplacing_ThenGuardRejectsAndRollsBack(string kind)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s); var b = await AssociationSetup.Items(fixture, s);
        await Members(s, a.Collection.Id, [a.Folder.Id], [a.Record.Id]); var p = await AssociationSetup.Profile(fixture, s, owner); await Routes(s, p, a.Collection.Id, [], []);
        var grant = await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection); var other = await ProfileSetup.Create(fixture, client); var newId = Guid.NewGuid(); var wrapper = Bytes(owner.WrapGrant(s.AccountId, a.Collection.PublicId, newId, other.Actor, client.Material.RecipientKey));
        var hook = new Hook("UPDATE cerberus.collection AS", async c =>
        {
            var sql = kind switch
            {
                "newFolder" => "INSERT INTO cerberus.collection_folder(account_id,collection_id,folder_id) VALUES (@owner,@target,@folder)",
                "newRecord" => "INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES (@owner,@target,@record)",
                "newGrant" => "INSERT INTO cerberus.collection_grant(public_id,collection_id,recipient_account_id,access,state,revision,recipient_key_envelope,concurrency_stamp) VALUES (@grant,@target,@recipient,0,0,1,@wrapper,@grant)",
                "grantPin" => "UPDATE cerberus.vault_protection SET revision=revision+1 WHERE account_id=@owner",
                "grantWrapper" => "UPDATE cerberus.collection_grant SET recipient_key_envelope='bad'::bytea WHERE id=@existing",
                "parent" => "UPDATE cerberus.record SET folder_id=@folder WHERE id=@oldRecord",
                "PC" => "DELETE FROM cerberus.profile_collection WHERE profile_id=@profile AND collection_id=@target",
                "revision" => "UPDATE cerberus.record SET revision=revision+1 WHERE id=@oldRecord",
                _ => "UPDATE cerberus.record SET revision=0 WHERE id=@oldRecord"
            };
            await using var write = c.Connection!.CreateCommand(); write.Transaction = c.Transaction; write.CommandText = sql;
            foreach (var (name, value) in new (string, object)[] { ("owner", s.InternalId), ("target", a.Collection.Id), ("folder", b.Folder.Id), ("record", b.Record.Id), ("grant", newId), ("recipient", other.InternalId), ("wrapper", wrapper), ("existing", grant.Id), ("oldRecord", a.Record.Id), ("profile", p) }) write.Parameters.Add(new NpgsqlParameter(name, value));
            await write.ExecuteNonQueryAsync();
        });
        var before = await Snapshot(s); var result = await new CollectionMembershipStore(new ProfileSetup.Factory(fixture, hook)).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, Input()), default);
        Assert.True(hook.Triggered); Assert.Equal(kind is "corruption" or "grantWrapper" ? "persistence_unavailable" : "revision_conflict", result.Error); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenActualProtectionRotation_WhenRacingMembership_ThenCurrentAuthorityOrInventoryWins(bool rotationFirst)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s);
        var change = new ProtectionChange(s.AccountId, 1, 1, "rotate-content", owner.Rewrap(), [new("account", s.AccountId, 1, ProfileSetup.Envelope(2)), new("collection", a.Collection.PublicId, 1, ProfileSetup.Envelope(2)), new("folder", a.Folder.PublicId, 1, ProfileSetup.Envelope(2)), new("record", a.Record.PublicId, 1, ProfileSetup.Envelope(2))]); var raw = Bytes(change);
        var challenge = (await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor, "change-protection", ProtocolBinary.Encode(SHA256.HashData(raw)), default)).Data!;
        var request = new ProtectionChangeRequest(s.Actor, s.Verifier, challenge.ChallengeId, owner.Sign(challenge), raw, change);
        var reached = SignalSource(); var release = SignalSource(); var waiting = SignalSource(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<VaultResult<CollectionMembershipDetails>> membership; Task<VaultResult<ProtectionChangeDetails>> rotation;
        if (rotationFirst)
        {
            rotation = new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture, new PauseAccount(reached, release))).ChangeAsync(request, ct.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            membership = new CollectionMembershipStore(new ProfileSetup.Factory(fixture, new Signal("account", waiting))).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, Input([a.Folder.PublicId])), ct.Token);
        }
        else
        {
            membership = new CollectionMembershipStore(new ProfileSetup.Factory(fixture, new Hook("UPDATE cerberus.collection AS", async _ => { reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }))).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, Input([a.Folder.PublicId])), ct.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            rotation = new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture, new Signal("account", waiting))).ChangeAsync(request, ct.Token);
        }
        try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(rotationFirst ? membership.IsCompleted : rotation.IsCompleted); } finally { release.TrySetResult(); }
        await Task.WhenAll(rotation, membership); Assert.Equal(rotationFirst ? "vault_access_denied" : null, membership.Result.Error); Assert.Equal(rotationFirst ? null : "revision_conflict", rotation.Result.Error);
        await using var db = fixture.CreateContext(); Assert.Equal(rotationFirst, (await db.VaultUnlockChallenges.SingleAsync(x => x.PublicId == challenge.ChallengeId)).Consumed);
    }

    [FunctionalFact]
    public async Task GivenHundredUnrelatedCollections_WhenReplacing_ThenBoundedTargetGraphAndNoForeignOrAncestorLocks()
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s); var parent = await Folder(s); await Sql("UPDATE cerberus.folder SET parent_folder_id={0} WHERE id={1}", parent.Id, a.Folder.Id);
        await Members(s, a.Collection.Id, [a.Folder.Id], [a.Record.Id]); await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection); var p = await AssociationSetup.Profile(fixture, s, owner); await Routes(s, p, a.Collection.Id, [], []); s = s with { Verifier = await Selected(s, p) };
        await using (var db = fixture.CreateContext()) { db.Collections.AddRange(Enumerable.Range(0, 100).Select(i => new VaultCollection { PublicId = Guid.NewGuid(), AccountId = i < 50 ? s.InternalId : r.InternalId, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch })); await db.SaveChangesAsync(); }
        var capture = new Capture(); Assert.Null((await new CollectionMembershipStore(new ProfileSetup.Factory(fixture, capture)).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, Input([a.Folder.PublicId], [a.Record.PublicId])), default)).Error);
        Assert.Equal(new[] { "account", "vault_access_session", "profile", "collection", "collection_grant", "folder", "record", "collection_folder", "collection_record" }, capture.Locks.Select(x => x.Table));
        Assert.Equal(s.Actor, Assert.Single(capture.Locks[0].Values)); Assert.Contains("FOR UPDATE", capture.Locks[0].Sql); Assert.Contains("FOR NO KEY UPDATE", capture.Locks[2].Sql); Assert.Contains("FOR UPDATE", capture.Locks[3].Sql); Assert.Contains("FOR SHARE", capture.Locks[4].Sql);
        Assert.Equal(new[] { a.Folder.Id }, capture.Locks.Single(x => x.Table == "folder").Values.OfType<long[]>().Single()); Assert.Equal(new[] { a.Record.Id }, capture.Locks.Single(x => x.Table == "record").Values.OfType<long[]>().Single());
        await using var check = fixture.CreateContext(); await check.Database.OpenConnectionAsync(); await using var explain = check.Database.GetDbConnection().CreateCommand(); explain.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + capture.Sql; foreach (var parameter in capture.Parameters) explain.Parameters.Add(parameter.Clone());
        using var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
        static IEnumerable<JsonElement> Nodes(JsonElement n) { yield return n; if (n.TryGetProperty("Plans", out var children)) foreach (var c in children.EnumerateArray()) foreach (var item in Nodes(c)) yield return item; }
        var target = Assert.Single(Nodes(plan.RootElement[0].GetProperty("Plan")), x => x.TryGetProperty("Subplan Name", out var name) && name.GetString() == "CTE target"); Assert.Equal(1d, target.GetProperty("Actual Rows").GetDouble());
    }

    [FunctionalTheory]
    [InlineData("actor", "authentication_required")][InlineData("handle", "vault_access_required")][InlineData("target", "validation_failed")][InlineData("input", "validation_failed")]
    [InlineData("revision", "validation_failed")][InlineData("time", "validation_failed")][InlineData("offset", "validation_failed")][InlineData("envelope", "validation_failed")]
    [InlineData("folders", "validation_failed")][InlineData("records", "validation_failed")][InlineData("duplicate", "validation_failed")][InlineData("emptyId", "validation_failed")]
    public async Task GivenInvalidInternalBoundary_WhenReplacing_ThenRejectBeforePersistence(string mode, string error)
    {
        var input = Input(); input = mode switch { "revision" => input with { ExpectedRevision = 0 }, "time" => input with { EditedAt = DateTimeOffset.MinValue }, "offset" => input with { EditedAt = input.EditedAt.ToOffset(TimeSpan.FromHours(1)) }, "envelope" => input with { Envelope = null! }, "folders" => input with { FolderIds = null! }, "records" => input with { RecordIds = null! }, "duplicate" => input with { FolderIds = [Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000001")] }, "emptyId" => input with { RecordIds = [Guid.Empty] }, _ => input };
        var result = await new CollectionMembershipStore(new ThrowingFactory(new Exception("Persistence must not be reached"))).SetAsync(new(mode == "actor" ? Guid.Empty : Guid.NewGuid(), mode == "handle" ? "" : "v", mode == "target" ? Guid.Empty : Guid.NewGuid(), mode == "input" ? null! : input), default);
        Assert.Equal(error, result.Error); Assert.Null(result.Data);
    }

    [FunctionalTheory][InlineData("target")][InlineData("member")][InlineData("retained")][InlineData("grant")][InlineData("links")][InlineData("count")]
    public async Task GivenLateUnexpectedCommittedProjection_WhenReplacing_ThenPostVerifyAllWritesAndRollback(string kind)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s); var b = await AssociationSetup.Items(fixture, s);
        await Members(s, a.Collection.Id, [a.Folder.Id], [a.Record.Id]); var grant = await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection);
        var hook = new AfterMutation("UPDATE cerberus.record SET", async c =>
        {
            var sql = kind switch { "target" => "UPDATE cerberus.collection SET revision=revision+1 WHERE id=@target", "member" => "UPDATE cerberus.folder SET envelope='bad'::bytea WHERE id=@newFolder", "retained" => "UPDATE cerberus.folder SET envelope='bad'::bytea WHERE id=@oldFolder", "grant" => "UPDATE cerberus.collection_grant SET revision=revision+1 WHERE id=@grant", "links" => "DELETE FROM cerberus.collection_folder WHERE collection_id=@target", _ => "SELECT 1" };
            await using var write = c.Connection!.CreateCommand(); write.Transaction = c.Transaction; write.CommandText = sql;
            foreach (var (name, value) in new[] { ("target", a.Collection.Id), ("newFolder", b.Folder.Id), ("oldFolder", a.Folder.Id), ("grant", grant.Id) }) write.Parameters.Add(new NpgsqlParameter(name, value));
            await write.ExecuteNonQueryAsync();
        }, kind == "count");
        var input = Input([a.Folder.PublicId, b.Folder.PublicId], [b.Record.PublicId]); var before = await Snapshot(s);
        var result = await new CollectionMembershipStore(new ProfileSetup.Factory(fixture, hook)).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, input), default);
        Assert.True(hook.Triggered); Assert.Equal(kind == "count" ? "revision_conflict" : "persistence_unavailable", result.Error); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenActiveGrantWithMissingRequiredRecipient_WhenReplacing_Then503()
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s); var grant = await AssociationSetup.Grant(fixture, s, owner, r, client, a.Collection);
        await using (var db = fixture.CreateContext())
        {
            await db.Database.OpenConnectionAsync(); await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
            try { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.collection_grant SET recipient_account_id={long.MaxValue} WHERE id={grant.Id}"); } finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
        }
        await Denied(s, a.Collection, Input([a.Folder.PublicId]), "persistence_unavailable");
    }

    [FunctionalFact]
    public async Task GivenReplacedMembership_WhenRotatingContent_ThenUseRetainedTypedInventoryAndUpdatedRevisions()
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s); await Members(s, a.Collection.Id, [a.Folder.Id], [a.Record.Id]);
        Assert.Null((await Set(s, a.Collection, Input([], [a.Record.PublicId]))).Error);
        var change = new ProtectionChange(s.AccountId, 1, 1, "rotate-content", owner.Rewrap(), [new("account", s.AccountId, 1, ProfileSetup.Envelope(2)), new("collection", a.Collection.PublicId, 2, ProfileSetup.Envelope(2)), new("folder", a.Folder.PublicId, 2, ProfileSetup.Envelope(2)), new("record", a.Record.PublicId, 1, ProfileSetup.Envelope(2))]); var raw = Bytes(change);
        var challenge = (await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor, "change-protection", ProtocolBinary.Encode(SHA256.HashData(raw)), default)).Data!;
        Assert.Null((await new VaultProtectionChangeStore(fixture).ChangeAsync(new(s.Actor, s.Verifier, challenge.ChallengeId, owner.Sign(challenge), raw, change), default)).Error);
        await using var db = fixture.CreateContext(); Assert.Equal(2, (await db.Collections.SingleAsync(x => x.Id == a.Collection.Id)).KeyEpoch); Assert.Empty(await db.CollectionFolders.Where(x => x.CollectionId == a.Collection.Id).ToArrayAsync()); Assert.Single(await db.CollectionRecords.Where(x => x.CollectionId == a.Collection.Id).ToArrayAsync());
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenGrantGrowthAtCollectionLock_WhenRacingMembership_ThenRejectUnheldGrantOrSerializeInsertion(bool membershipFirst)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var r = await ProfileSetup.Create(fixture, client); var a = await AssociationSetup.Items(fixture, s);
        var id = Guid.NewGuid(); var grant = new CollectionGrant { PublicId = id, CollectionId = a.Collection.Id, RecipientAccountId = r.InternalId, RecipientKeyEnvelope = Bytes(owner.WrapGrant(s.AccountId, a.Collection.PublicId, id, r.Actor, client.Material.RecipientKey)) };
        var reached = SignalSource(); var release = SignalSource(); var waiting = SignalSource(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<VaultResult<CollectionMembershipDetails>> membership; var input = Input([a.Folder.PublicId]);
        if (membershipFirst)
        {
            membership = new CollectionMembershipStore(new ProfileSetup.Factory(fixture, new Hook("UPDATE cerberus.collection AS", async _ => { reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }))).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, input), ct.Token);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var writer = new ProfileSetup.Factory(fixture, new ReaderSignal("INSERT INTO cerberus.collection_grant", waiting)).CreateDbContext(); writer.CollectionGrants.Add(grant); var inserted = writer.SaveChangesAsync(ct.Token);
            try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(inserted.IsCompleted); } finally { release.TrySetResult(); }
            Assert.Null((await membership).Error); await inserted;
        }
        else
        {
            await using var writer = fixture.CreateContext(); await using var tx = await writer.Database.BeginTransactionAsync(ct.Token); writer.CollectionGrants.Add(grant); await writer.SaveChangesAsync(ct.Token);
            membership = new CollectionMembershipStore(new ProfileSetup.Factory(fixture, new Signal("collection", waiting))).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, input), ct.Token);
            try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(membership.IsCompleted); } finally { await tx.CommitAsync(ct.Token); }
            Assert.Equal("revision_conflict", (await membership).Error); Assert.Null((await Set(s, a.Collection, input)).Error);
        }
        await using var check = fixture.CreateContext(); Assert.Equal(Bytes(grant), Bytes(await check.CollectionGrants.AsNoTracking().SingleAsync(x => x.Id == grant.Id)));
        Assert.Null((await new FolderReadStore(fixture).ReadAsync(new(r.Actor, r.Verifier, a.Folder.PublicId), default)).Error);
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenExpiryAtAuthorizationLinearization_WhenReplacing_ThenBeforeDeniedAndAfterCommits(bool after)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s); var deadline = DateTimeOffset.UtcNow.AddSeconds(1.2);
        await Sql("UPDATE cerberus.vault_access_session SET expires_at={0} WHERE handle_verifier={1}", deadline, s.Verifier); var before = await Snapshot(s); var input = Input([a.Folder.PublicId]);
        DbCommandInterceptor hook = after ? new AfterMutation("UPDATE cerberus.collection AS", async _ => await Past(deadline), false) : new Hook("UPDATE cerberus.collection AS", async _ => await Past(deadline));
        var result = await new CollectionMembershipStore(new ProfileSetup.Factory(fixture, hook)).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, input), default);
        if (after) await Success(s, a.Collection, input, before, result); else { Assert.Equal("vault_access_denied", result.Error); Assert.Equal(before, await Snapshot(s)); }
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenSequenceFailsAfterTargetWrite_WhenBumpingMembers_ThenWholeTransactionRollsBack(bool exhausted)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s); var before = await Snapshot(s); await using var db = fixture.CreateContext();
        var old = await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();
        try
        {
            var hook = new AfterMutation("UPDATE cerberus.collection AS", async c => { await using var reset = c.Connection!.CreateCommand(); reset.Transaction = c.Transaction; reset.CommandText = "SELECT setval('cerberus.server_sequence',@value,true)"; reset.Parameters.Add(new NpgsqlParameter("value", exhausted ? ProtocolBinary.MaxInteger : 1)); await reset.ExecuteNonQueryAsync(); }, false);
            var result = await new CollectionMembershipStore(new ProfileSetup.Factory(fixture, hook)).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, Input([a.Folder.PublicId])), default);
            Assert.True(hook.Triggered); Assert.Equal(exhausted ? "revision_conflict" : "persistence_unavailable", result.Error); Assert.Equal(before, await Snapshot(s));
        }
        finally { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{old},true)"); }
    }

    [FunctionalTheory]
    [InlineData("folder", false)][InlineData("folder", true)][InlineData("record", false)][InlineData("record", true)]
    [InlineData("folderAncestor", false)][InlineData("folderAncestor", true)][InlineData("recordAncestor", false)][InlineData("recordAncestor", true)]
    [InlineData("private", true)]
    public async Task GivenActiveRequestedOrRequiredAncestorWithPurgeDeadline_WhenRetaining_ThenRequired503OrHidden404(string kind, bool selected)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s); var parent = await Folder(s); var child = await Folder(s, parent.Id); var r = await Record(s, parent.Id);
        await Members(s, a.Collection.Id, [child.Id], [r.Id]);
        if (selected) { var p = await AssociationSetup.Profile(fixture, s, owner); await Routes(s, p, a.Collection.Id, [], []); s = s with { Verifier = await Selected(s, p) }; }
        var table = kind == "record" ? "record" : "folder"; var id = kind == "record" ? r.Id : kind == "folder" ? child.Id : kind == "private" ? a.Folder.Id : parent.Id;
        await Sql("UPDATE cerberus." + table + " SET purge_at=statement_timestamp() WHERE id={0}", id);
        var input = Input(kind == "private" ? [a.Folder.PublicId] : kind == "recordAncestor" ? [] : [child.PublicId], kind == "folderAncestor" || kind == "private" ? [] : [r.PublicId]);
        await Denied(s, a.Collection, input, kind == "private" ? "not_found" : "persistence_unavailable");
    }

    [FunctionalTheory][InlineData("target")][InlineData("folder")][InlineData("record")][InlineData("lateFolder")]
    public async Task GivenMissingPostWriteProjection_WhenReplacing_Then503AndFullRollback(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); var a = await AssociationSetup.Items(fixture, s);
        if (kind != "target") await Members(s, a.Collection.Id, kind == "record" ? [] : [a.Folder.Id], kind == "folder" ? [] : [a.Record.Id]);
        var stage = kind == "target" ? "UPDATE cerberus.collection AS" : kind == "folder" ? "UPDATE cerberus.folder SET" : "UPDATE cerberus.record SET";
        var hook = new AfterMutation(stage, async c =>
        {
            await using var write = c.Connection!.CreateCommand(); write.Transaction = c.Transaction;
            write.CommandText = "DELETE FROM cerberus." + (kind == "target" ? "collection" : kind == "record" ? "record" : "folder") + " WHERE id=@id";
            write.Parameters.Add(new NpgsqlParameter("id", kind == "target" ? a.Collection.Id : kind == "record" ? a.Record.Id : a.Folder.Id)); await write.ExecuteNonQueryAsync();
        }, false);
        var before = await Snapshot(s); var result = await new CollectionMembershipStore(new ProfileSetup.Factory(fixture, hook)).SetAsync(new(s.Actor, s.Verifier, a.Collection.PublicId, Input()), default);
        Assert.True(hook.Triggered); Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s));
    }

    private async Task<ArturRios.Cerberus.Domain.Profiles.Profile> ProfileRow(long id) { await using var db = fixture.CreateContext(); return await db.Profiles.AsNoTracking().SingleAsync(x => x.Id == id); }
    private static TaskCompletionSource SignalSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Past(DateTimeOffset deadline) { var delay = deadline - DateTimeOffset.UtcNow; if (delay > TimeSpan.Zero) await Task.Delay(delay + TimeSpan.FromMilliseconds(70)); }
    private static CollectionMembershipInput Input(Guid[]? folders = null, Guid[]? records = null) => new(1, ProfileSetup.Envelope(), DateTimeOffset.UnixEpoch.AddTicks(19), records ?? [], folders ?? []);
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ProtectionFixture.Json);
    private Task<VaultResult<CollectionMembershipDetails>> Set(ProfileSetup.State s, VaultCollection c, CollectionMembershipInput input) => new CollectionMembershipStore(fixture).SetAsync(new(s.Actor, s.Verifier, c.PublicId, input), default);
    private async Task Denied(ProfileSetup.State s, VaultCollection c, CollectionMembershipInput input, string error)
    { var before = await Snapshot(s); var result = await Set(s, c, input); Assert.Equal(error, result.Error); Assert.Null(result.Data); Assert.Equal(before, await Snapshot(s)); }
    private async Task Sql(string sql, params object[] parameters) { await using var db = fixture.CreateContext(); await db.Database.ExecuteSqlRawAsync(sql, parameters); }
    private async Task Terminal(string kind, Guid id) { await using var db = fixture.CreateContext(); db.TerminalErasures.Add(new() { ResourceKind = kind, ResourceId = id, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
    private async Task<VaultFolder> Folder(ProfileSetup.State s, long? parent = null)
    { var f = new VaultFolder { PublicId = Guid.NewGuid(), AccountId = s.InternalId, ParentFolderId = parent, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch }; await using var db = fixture.CreateContext(); db.Folders.Add(f); await db.SaveChangesAsync(); return f; }
    private async Task<VaultRecord> Record(ProfileSetup.State s, long? parent = null)
    { var r = new VaultRecord { PublicId = Guid.NewGuid(), AccountId = s.InternalId, FolderId = parent, Envelope = Bytes(ProfileSetup.Envelope()), EditedAt = DateTimeOffset.UnixEpoch }; await using var db = fixture.CreateContext(); db.Records.Add(r); await db.SaveChangesAsync(); return r; }
    private async Task Members(ProfileSetup.State s, long collection, long[] folders, long[] records)
    { await using var db = fixture.CreateContext(); db.CollectionFolders.AddRange(folders.Select(x => new CollectionFolder { AccountId = s.InternalId, CollectionId = collection, FolderId = x })); db.CollectionRecords.AddRange(records.Select(x => new CollectionRecord { AccountId = s.InternalId, CollectionId = collection, RecordId = x })); await db.SaveChangesAsync(); }
    private async Task Routes(ProfileSetup.State s, long profile, long collection, long[] folders, long[] records)
    { await using var db = fixture.CreateContext(); db.ProfileCollections.Add(new() { ProfileId = profile, CollectionId = collection }); db.ProfileFolders.AddRange(folders.Select(x => new ProfileFolder { AccountId = s.InternalId, ProfileId = profile, FolderId = x })); db.ProfileRecords.AddRange(records.Select(x => new ProfileRecord { AccountId = s.InternalId, ProfileId = profile, RecordId = x })); await db.SaveChangesAsync(); }
    private async Task<string> Selected(ProfileSetup.State s, long profile)
    { var v = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); await using var db = fixture.CreateContext(); db.VaultAccessSessions.Add(new() { AccountId = s.InternalId, ProfileId = profile, HandleVerifier = v, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 }); await db.SaveChangesAsync(); return v; }
    private async Task<string> Snapshot(ProfileSetup.State s)
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
              'account',(SELECT to_jsonb(a) FROM cerberus.account a WHERE a.id={s.InternalId}),
              'protection',(SELECT to_jsonb(p) FROM cerberus.vault_protection p WHERE p.account_id={s.InternalId}),
              'sessions',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.vault_access_session r WHERE r.account_id={s.InternalId}),'[]'),
              'profiles',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.profile r WHERE r.account_id={s.InternalId}),'[]'),
              'folders',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.folder r WHERE r.account_id={s.InternalId}),'[]'),
              'records',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.record r WHERE r.account_id={s.InternalId}),'[]'),
              'collections',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.collection r WHERE r.account_id={s.InternalId}),'[]'),
              'PF',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.profile_id,r.folder_id) FROM cerberus.profile_folder r WHERE r.account_id={s.InternalId}),'[]'),
              'PR',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.profile_id,r.record_id) FROM cerberus.profile_record r WHERE r.account_id={s.InternalId}),'[]'),
              'PC',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.profile_id,r.collection_id) FROM cerberus.profile_collection r JOIN cerberus.profile p ON p.id=r.profile_id WHERE p.account_id={s.InternalId}),'[]'),
              'CF',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.collection_id,r.folder_id) FROM cerberus.collection_folder r WHERE r.account_id={s.InternalId}),'[]'),
              'CR',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.collection_id,r.record_id) FROM cerberus.collection_record r WHERE r.account_id={s.InternalId}),'[]'),
              'grants',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.collection_grant r JOIN cerberus.collection c ON c.id=r.collection_id WHERE c.account_id={s.InternalId} OR r.recipient_account_id={s.InternalId}),'[]'))::text AS "Value"
            """).SingleAsync();
    }
    private async Task Success(ProfileSetup.State s, VaultCollection target, CollectionMembershipInput input, string before, VaultResult<CollectionMembershipDetails> result)
    {
        Assert.Null(result.Error); Assert.NotNull(result.Data); Assert.Equal(target.PublicId, result.Data.CollectionId); Assert.Equal(input.ExpectedRevision + 1, result.Data.Revision); Assert.True(result.Data.ServerSequence > target.ServerSequence); Assert.InRange(result.Data.ServerSequence, 1, ProtocolBinary.MaxInteger);
        Assert.Equal(input.EditedAt.AddTicks(-(input.EditedAt.Ticks % 10)), result.Data.EditedAt); Assert.Equal(input.FolderIds.Order(), result.Data.FolderIds); Assert.Equal(input.RecordIds.Order(), result.Data.RecordIds);
        using var output = JsonDocument.Parse(Bytes(result.Data)); Assert.Equal(new[] { "collectionId", "editedAt", "folderIds", "recordIds", "revision", "serverSequence" }, output.RootElement.EnumerateObject().Select(x => x.Name).Order());
        await using var db = fixture.CreateContext(); var c = await db.Collections.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.Equal(Bytes(input.Envelope), c.Envelope); Assert.Equal(result.Data.Revision, c.Revision); Assert.Equal(result.Data.EditedAt, c.EditedAt); Assert.Equal(result.Data.ServerSequence, c.ServerSequence); Assert.Equal(target.KeyEpoch, c.KeyEpoch);
        var old = JsonNode.Parse(before)!.AsObject(); var now = JsonNode.Parse(await Snapshot(s))!.AsObject();
        foreach (var (links, table, idColumn, ids) in new[] { ("CF", "folders", "folder_id", input.FolderIds), ("CR", "records", "record_id", input.RecordIds) })
        {
            var previousLinks = old[links]!.AsArray().Where(x => x!["collection_id"]!.GetValue<long>() == target.Id).ToArray();
            var currentLinks = now[links]!.AsArray().Where(x => x!["collection_id"]!.GetValue<long>() == target.Id).ToArray();
            var previousIds = previousLinks.Select(x => x![idColumn]!.GetValue<long>()).ToArray(); var currentIds = currentLinks.Select(x => x![idColumn]!.GetValue<long>()).ToArray();
            Assert.Equal(ids.Order(), now[table]!.AsArray().Where(x => currentIds.Contains(x!["id"]!.GetValue<long>())).Select(x => x!["public_id"]!.GetValue<Guid>()).Order());
            foreach (var row in now[table]!.AsArray())
            {
                var id = row!["id"]!.GetValue<long>(); if (previousIds.Contains(id) == currentIds.Contains(id) || row["deleted_at"] is not null) continue;
                var prior = old[table]!.AsArray().Single(x => x!["id"]!.GetValue<long>() == id)!;
                Assert.Equal(prior["revision"]!.GetValue<long>() + 1, row["revision"]!.GetValue<long>()); Assert.True(row["server_sequence"]!.GetValue<long>() > prior["server_sequence"]!.GetValue<long>()); Assert.NotEqual(prior["concurrency_stamp"]!.ToJsonString(), row["concurrency_stamp"]!.ToJsonString());
                foreach (var name in new[] { "revision", "server_sequence", "concurrency_stamp" }) row[name] = prior[name]!.DeepClone();
            }
            foreach (var link in previousLinks) old[links]!.AsArray().Remove(link); foreach (var link in currentLinks) now[links]!.AsArray().Remove(link);
        }
        var oldTarget = old["collections"]!.AsArray().Single(x => x!["id"]!.GetValue<long>() == target.Id)!; var newTarget = now["collections"]!.AsArray().Single(x => x!["id"]!.GetValue<long>() == target.Id)!;
        Assert.NotEqual(oldTarget["concurrency_stamp"]!.ToJsonString(), newTarget["concurrency_stamp"]!.ToJsonString());
        foreach (var name in new[] { "envelope", "edited_at", "revision", "server_sequence", "concurrency_stamp" }) newTarget[name] = oldTarget[name]!.DeepClone();
        Assert.Equal(old.ToJsonString(), now.ToJsonString());
    }

    private sealed class AfterMutation(string text, Func<DbCommand, Task> action, bool wrongCount) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken ct = default)
        { if (!Triggered && r > 0 && c.CommandText.Contains(text)) { Triggered = true; await action(c); if (wrongCount) return 0; } return r; }
    }
    private sealed class ReaderSignal(string text, TaskCompletionSource reached) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { if (c.CommandText.Contains(text)) reached.TrySetResult(); return ValueTask.FromResult(r); }
    }
    private sealed class Signal(string table, TaskCompletionSource reached) : DbCommandInterceptor
    {
        private void Check(DbCommand c) { if (c.CommandText.Contains("FROM cerberus." + table + " ") && c.CommandText.Contains("FOR ")) reached.TrySetResult(); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default) { Check(c); return ValueTask.FromResult(r); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { Check(c); return ValueTask.FromResult(r); }
    }
    private sealed class PauseAccount(TaskCompletionSource reached, TaskCompletionSource release) : DbCommandInterceptor
    {
        private bool used;
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken ct = default)
        { if (!used && c.CommandText.Contains("FROM cerberus.account ") && c.CommandText.Contains("FOR ")) { used = true; reached.TrySetResult(); await release.Task.WaitAsync(ct); } return r; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c, CommandExecutedEventData d, DbDataReader r, CancellationToken ct = default)
        { if (!used && c.CommandText.Contains("FROM cerberus.account ") && c.CommandText.Contains("FOR ")) { used = true; reached.TrySetResult(); await release.Task.WaitAsync(ct); } return r; }
    }
    private sealed class Hook(string text, Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default) { if (!Triggered && c.CommandText.Contains(text)) { Triggered = true; await action(c); } return r; }
    }
    private sealed class Capture : DbCommandInterceptor
    {
        public List<(string Table, string Sql, object[] Values)> Locks { get; } = []; public string Sql { get; private set; } = ""; public NpgsqlParameter[] Parameters { get; private set; } = [];
        private void Note(DbCommand c) { if (c.CommandText.Contains("FOR ")) { var table = new[] { "account", "vault_access_session", "profile", "collection", "collection_grant", "folder", "record", "collection_folder", "collection_record" }.Single(x => c.CommandText.Contains("FROM cerberus." + x + " ")); Locks.Add((table, c.CommandText, c.Parameters.Cast<DbParameter>().Select(x => x.Value!).ToArray())); } }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default) { Note(c); return ValueTask.FromResult(r); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
        { Note(c); if (Sql.Length == 0 && c.CommandText.StartsWith("WITH RECURSIVE")) { Sql = c.CommandText; Parameters = c.Parameters.Cast<NpgsqlParameter>().Select(x => x.Clone()).ToArray(); } return ValueTask.FromResult(r); }
    }
    private sealed class ThrowingFactory(Exception failure) : IDbContextFactory<AppDbContext>
    { public AppDbContext CreateDbContext() => throw failure; public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromException<AppDbContext>(failure); }
    private sealed class Fault(string stage) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; } private int matches;
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int result, CancellationToken ct = default)
        {
            var sql = stage switch { "target" => "UPDATE cerberus.collection AS", "deleteFolder" => "DELETE FROM cerberus.collection_folder", "deleteRecord" => "DELETE FROM cerberus.collection_record", "insertFolder" => "INSERT INTO cerberus.collection_folder", "insertRecord" => "INSERT INTO cerberus.collection_record", _ => "UPDATE cerberus." + (stage.StartsWith("folder") ? "folder" : "record") + " SET" };
            if (result > 0 && c.CommandText.Contains(sql) && ++matches == (stage.EndsWith('2') ? 2 : 1)) { Triggered = true; throw new TimeoutException("controlled post-mutation failure"); } return ValueTask.FromResult(result);
        }
    }
}
