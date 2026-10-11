using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class CollectionListStoreTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenForeignTrashAndTerminalRows_WhenPaging_ThenFilterBeforeTakeAndKeepInitialBoundary()
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var foreign=await ProfileSetup.Create(fixture,owner);var trash=await Collection(s);await Collection(foreign);var terminal=await Collection(s);var first=await Collection(s);var second=await Collection(s);
        await using(var db=fixture.CreateContext()){await db.Collections.Where(x=>x.Id==trash.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,DateTimeOffset.UtcNow));db.TerminalErasures.Add(new(){ResourceId=terminal.PublicId,ResourceKind="collection",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var before=await Snapshot(s);var page=await List(s,1);Assert.Null(page.Error);Assert.Equal(first.PublicId,Assert.Single(page.Data!.Items).CollectionId);Assert.True(page.Data.HasMore);Assert.Equal(second.ServerSequence,page.Data.Boundary);Assert.Equal(before,await Snapshot(s));var later=await Collection(s);var next=await List(s,1,first.ServerSequence,page.Data.Boundary);Assert.Null(next.Error);Assert.Equal(second.PublicId,Assert.Single(next.Data!.Items).CollectionId);Assert.False(next.Data.HasMore);Assert.DoesNotContain(next.Data.Items,x=>x.CollectionId==later.PublicId);Assert.Equal(first.Envelope,page.Data.Items[0].Envelope);
    }
    [FunctionalTheory][InlineData("revoked")][InlineData("wrongRecipient")][InlineData("unknownAccess")][InlineData("zeroRevision")][InlineData("unsafeRevision")][InlineData("grantTerminal")][InlineData("collectionTrash")][InlineData("collectionTerminal")][InlineData("ownerClosing")][InlineData("ownerTerminal")]
    public async Task GivenHiddenSharedAuthorityOrContent_WhenListing_ThenOmitBeforePaginationWithoutNativeInspection(string kind)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var items=await AssociationSetup.Items(fixture,foreign);var grant=await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,items.Collection);
        await using(var db=fixture.CreateContext()){var g=await db.CollectionGrants.SingleAsync(x=>x.Id==grant.Id);g.RecipientKeyEnvelope="bad"u8.ToArray();if(kind=="revoked")g.State=CollectionGrantState.Revoked;if(kind=="wrongRecipient")g.RecipientAccountId=foreign.InternalId;if(kind=="unknownAccess")g.Access=(CollectionGrantAccess)99;if(kind=="zeroRevision")g.Revision=0;if(kind=="unsafeRevision")g.Revision=ProtocolBinary.MaxInteger+1;if(kind=="collectionTrash")await db.Collections.Where(x=>x.Id==items.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.DeletedAt,DateTimeOffset.UtcNow));if(kind=="ownerClosing")await db.Accounts.Where(x=>x.Id==foreign.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));if(kind.EndsWith("Terminal",StringComparison.Ordinal))db.TerminalErasures.Add(new(){ResourceId=kind=="grantTerminal"?grant.PublicId:kind=="collectionTerminal"?items.Collection.PublicId:kind=="ownerTerminal"?foreign.AccountId:items.Collection.PublicId,ResourceKind=kind=="grantTerminal"?"grant":kind=="collectionTerminal"?"collection":kind=="ownerTerminal"?"account":"collection",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var r=await List(s,1);Assert.Null(r.Error);Assert.Empty(r.Data!.Items);Assert.False(r.Data.HasMore);Assert.Equal(0,r.Data.Boundary);
    }
    [FunctionalTheory][InlineData("blob")][InlineData("signature")][InlineData("owner")][InlineData("collection")][InlineData("grant")][InlineData("recipient")][InlineData("revision")][InlineData("epoch")][InlineData("collectionEnvelope")][InlineData("ownerPins")][InlineData("recipientPins")][InlineData("missingOwnerPins")][InlineData("missingRecipientPins")][InlineData("collectionNumericString")][InlineData("nativeEpoch")][InlineData("signedEpoch")][InlineData("signedRevision")]
    public async Task GivenCorruptRelevantNativeGrant_WhenListing_Then503WithoutPartialContent(string kind)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var items=await AssociationSetup.Items(fixture,foreign);var grant=await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,items.Collection);await Collection(s);
        await using(var db=fixture.CreateContext()){var g=await db.CollectionGrants.SingleAsync(x=>x.Id==grant.Id);if(kind=="blob")g.RecipientKeyEnvelope="bad"u8.ToArray();if(kind is "owner" or "collection" or "grant" or "recipient")g.RecipientKeyEnvelope=Bytes(owner.WrapGrant(kind=="owner"?Guid.NewGuid():foreign.AccountId,kind=="collection"?Guid.NewGuid():items.Collection.PublicId,kind=="grant"?Guid.NewGuid():grant.PublicId,kind=="recipient"?Guid.NewGuid():s.Actor,recipient.Material.RecipientKey));if(kind=="signature"){var wrapper=JsonSerializer.Deserialize<RecipientEnvelope>(g.RecipientKeyEnvelope,ProtectionFixture.Json)!;ProtocolBinary.TryDecode(wrapper.Signature,null,out var bytes);bytes[0]^=1;g.RecipientKeyEnvelope=Bytes(wrapper with{Signature=ProtocolBinary.Encode(bytes)});}if(kind=="revision")g.Revision=2;if(kind=="signedEpoch")g.RecipientKeyEnvelope=Bytes(owner.WrapGrant(foreign.AccountId,items.Collection.PublicId,grant.PublicId,s.Actor,recipient.Material.RecipientKey,epoch:2));if(kind=="signedRevision")g.RecipientKeyEnvelope=Bytes(owner.WrapGrant(foreign.AccountId,items.Collection.PublicId,grant.PublicId,s.Actor,recipient.Material.RecipientKey,revision:2));if(kind=="nativeEpoch")await db.Collections.Where(x=>x.Id==items.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.KeyEpoch,2).SetProperty(c=>c.Envelope,Bytes(ProfileSetup.Envelope(2))));if(kind=="epoch")await db.Collections.Where(x=>x.Id==items.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.KeyEpoch,2));if(kind=="collectionNumericString")await db.Collections.Where(x=>x.Id==items.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(items.Collection.Envelope).Replace("\"keyEpoch\":1","\"keyEpoch\":\"1\""))));if(kind=="collectionEnvelope")await db.Collections.Where(x=>x.Id==items.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,"bad"u8.ToArray()));if(kind is "ownerPins" or "recipientPins")await db.VaultProtections.Where(x=>x.AccountId==(kind=="ownerPins"?foreign.InternalId:s.InternalId)).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Material,"bad"u8.ToArray()));if(kind is "missingOwnerPins" or "missingRecipientPins")await db.VaultProtections.Where(x=>x.AccountId==(kind=="missingOwnerPins"?foreign.InternalId:s.InternalId)).ExecuteDeleteAsync();await db.SaveChangesAsync();}
        var before=await Snapshot(s);var foreignBefore=await Snapshot(foreign);var r=await List(s,1);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));Assert.Equal(foreignBefore,await Snapshot(foreign));
    }
    [FunctionalFact]
    public async Task GivenUnselectedGrantedCollectionWithCorruptWrapper_WhenListing_ThenDoNotDiscloseOrDenySelectedOwnContent()
    {using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var scoped=new ProtectionFixture();var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var items=await AssociationSetup.Items(fixture,foreign);var g=await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,items.Collection);var visible=await Collection(s);var p=await Profile(s,recipient,scoped,[visible.PublicId]);await using(var db=fixture.CreateContext())await db.CollectionGrants.Where(x=>x.Id==g.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));var r=await List(s with{Verifier=await Selected(s,p.Id)});Assert.Null(r.Error);Assert.Equal(visible.PublicId,Assert.Single(r.Data!.Items).CollectionId);}
    [FunctionalTheory][InlineData("missing","not_found")][InlineData("closing","not_found")][InlineData("terminal","not_found")][InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")][InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")][InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")][InlineData("nullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)]
    public async Task GivenCurrentAccountOrSessionState_WhenListing_ThenRevalidateWithoutMutation(string kind,string? error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var record=await Collection(s);await using(var db=fixture.CreateContext()){var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);if(kind=="closing")a.State=AccountState.ClosurePending;if(kind=="terminal")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="zeroPolicy")v.PolicyRevision=0;if(kind=="zeroGeneration")v.RevocationGeneration=0;if(kind is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(kind is "noExpiry" or "nullExpiry")v.ExpiresAt=null;await db.SaveChangesAsync();}var before=await Snapshot(s);var r=await List(s with{Actor=kind=="missing"?Guid.NewGuid():s.Actor,Verifier=kind=="access"?new string('a',64):s.Verifier});Assert.Equal(error,r.Error);if(error is null)Assert.Equal(record.PublicId,Assert.Single(r.Data!.Items).CollectionId);else Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("valid",null)][InlineData("dangling","vault_access_denied")][InlineData("foreign","vault_access_denied")][InlineData("trash","vault_access_denied")][InlineData("terminal","vault_access_denied")]
    public async Task GivenSelectedProfileLifecycle_WhenListing_ThenRequireCurrentOwnedActiveSelection(string kind,string? error)
    {using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var foreign=await ProfileSetup.Create(fixture,owner);var own=await Collection(s);var p=await Profile(kind=="foreign"?foreign:s,owner,scoped,kind=="foreign"?[]:[own.PublicId]);var verifier=await Selected(s,kind=="dangling"?long.MaxValue:p.Id);await using(var db=fixture.CreateContext()){if(kind=="trash")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));if(kind=="terminal"){db.TerminalErasures.Add(new(){ResourceId=p.PublicId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}var r=await List(s with{Verifier=verifier});Assert.Equal(error,r.Error);if(error is null)Assert.Equal(own.PublicId,Assert.Single(r.Data!.Items).CollectionId);else Assert.Null(r.Data);}
    [FunctionalTheory][InlineData("zero")][InlineData("negative")][InlineData("unsafe")][InlineData("duplicate")]
    public async Task GivenCorruptPermittedOrdering_WhenListing_Then503BeforeKeysetCanHideOrSplitIt(string kind)
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var first=await Collection(s);var second=await Collection(s);await using(var db=fixture.CreateContext())await db.Collections.Where(x=>x.Id==second.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.ServerSequence,kind=="zero"?0:kind=="negative"?-1:kind=="unsafe"?ProtocolBinary.MaxInteger+1:first.ServerSequence));var r=await List(s,1);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);}
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenCorruptHiddenOrdering_WhenListing_ThenDoNotInspectOutOfScopeOrTrashedRows(bool selected)
    {using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var foreign=await ProfileSetup.Create(fixture,owner);var hidden=await Collection(s);var unrelated=await Collection(foreign);var visible=await Collection(s);var p=await Profile(s,owner,scoped,[visible.PublicId]);await using(var db=fixture.CreateContext()){await db.Collections.Where(x=>x.Id==hidden.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.ServerSequence,0).SetProperty(r=>r.DeletedAt,selected?(DateTimeOffset?)null:DateTimeOffset.UtcNow));await db.Collections.Where(x=>x.Id==unrelated.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.ServerSequence,0));}var r=await List(selected?s with{Verifier=await Selected(s,p.Id)}:s,1);Assert.Null(r.Error);Assert.Equal(visible.PublicId,Assert.Single(r.Data!.Items).CollectionId);}
    [FunctionalFact]
    public async Task GivenNativeSharedContent_WhenReading_ThenUseOneSqlSnapshotEvenIfGrantRevokesAfterStatement()
    {using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var items=await AssociationSetup.Items(fixture,foreign);var grant=await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,items.Collection);var interceptor=new RevokeAfterRead(fixture,grant.Id);var store=new CollectionListStore(new ProfileSetup.Factory(fixture,interceptor));var r=await store.ListAsync(new(s.Actor,s.Verifier,10,0,null),default);Assert.Null(r.Error);Assert.Equal(items.Collection.PublicId,Assert.Single(r.Data!.Items).CollectionId);Assert.Equal(1,interceptor.Readers);var next=await List(s);Assert.Null(next.Error);Assert.Empty(next.Data!.Items);}
    [FunctionalFact]
    public async Task GivenExpiryImmediatelyBeforeSql_WhenListing_ThenDatabaseTimeDeniesWithoutMutation()
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);await Collection(s);var deadline=DateTimeOffset.UtcNow.AddSeconds(1);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));var before=await Snapshot(s);var r=await new CollectionListStore(new ProfileSetup.Factory(fixture,new ExpireBeforeRead(deadline))).ListAsync(new(s.Actor,s.Verifier,10,0,null),default);Assert.Equal("vault_access_denied",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("record")][InlineData("profile")][InlineData("collection")][InlineData("grant")][InlineData("account")][InlineData("folder")]
    public async Task GivenSameUuidTerminalKind_WhenListing_ThenOnlyCollectionMarkerHidesCollection(string kind)
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var folder=await Collection(s);await using(var db=fixture.CreateContext()){db.TerminalErasures.Add(new(){ResourceId=folder.PublicId,ResourceKind=kind,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}var r=await List(s);Assert.Null(r.Error);Assert.Equal(kind=="collection"?0:1,r.Data!.Items.Count);if(kind!="collection")Assert.Equal(folder.PublicId,Assert.Single(r.Data.Items).CollectionId);}
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenInternalCancellationOrTimeout_WhenListing_Then503WithoutReadMutation(bool cancellation)
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);await Collection(s);var before=await Snapshot(s);var r=await new CollectionListStore(new ProfileSetup.Factory(fixture,new FailRead(cancellation))).ListAsync(new(s.Actor,s.Verifier,10,0,null),default);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));}
    private sealed class FailRead(bool cancellation):DbCommandInterceptor
    {public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> result,CancellationToken ct=default){if(cancellation)throw new OperationCanceledException();throw new TimeoutException();}}

    [FunctionalTheory]
    [InlineData(CollectionGrantAccess.ReadOnly, false)][InlineData(CollectionGrantAccess.ReadOnly, true)]
    [InlineData(CollectionGrantAccess.ReadWrite, false)][InlineData(CollectionGrantAccess.ReadWrite, true)]
    public async Task GivenNativeGrantAndOwnedCollections_WhenListing_ThenApplyExactSelectedCollectionLinks(CollectionGrantAccess access, bool selected)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var scoped = new ProtectionFixture();
        var foreign = await ProfileSetup.Create(fixture, owner); var s = await ProfileSetup.Create(fixture, recipient);
        var shared = await Collection(foreign); var unselected = await Collection(foreign); var own = await Collection(s); var other = await Collection(s);
        await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, shared, access);
        await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, unselected, access);
        var p = await Profile(s, recipient, scoped, [own.PublicId, shared.PublicId]);
        var request = selected ? s with { Verifier = await Selected(s, p.Id) } : s;
        var before = await Snapshot(s); var ownerBefore = await Snapshot(foreign);
        var result = await List(request);
        Assert.Null(result.Error);
        Assert.Equal((selected ? new[] { own.PublicId, shared.PublicId } : new[] { own.PublicId, other.PublicId, shared.PublicId, unselected.PublicId }).Order(), result.Data!.Items.Select(x => x.CollectionId).Order());
        Assert.Equal(shared.Envelope, result.Data.Items.Single(x => x.CollectionId == shared.PublicId).Envelope);
        Assert.Equal(before, await Snapshot(s));
        Assert.Equal(ownerBefore, await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData("folder")][InlineData("record")]
    public async Task GivenSelectedContentWithoutCollectionLink_WhenListing_ThenDoNotInferCollectionPermission(string kind)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner);
        var resources = await AssociationSetup.Items(fixture, s);
        await using (var db = fixture.CreateContext()) {
            db.CollectionFolders.Add(new() { AccountId = s.InternalId, CollectionId = resources.Collection.Id, FolderId = resources.Folder.Id });
            db.CollectionRecords.Add(new() { AccountId = s.InternalId, CollectionId = resources.Collection.Id, RecordId = resources.Record.Id }); await db.SaveChangesAsync();
        }
        var input = ProfileSetup.Input(s, owner, scoped) with { FolderIds = kind == "folder" ? [resources.Folder.PublicId] : [], RecordIds = kind == "record" ? [resources.Record.PublicId] : [] };
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor, s.Verifier, input), default)).Error);
        await using var context = fixture.CreateContext(); var p = await context.Profiles.SingleAsync(x => x.PublicId == input.ProfileId);
        var selected = s with { Verifier = await Selected(s, p.Id) }; var before = await Snapshot(s);
        var result = await List(selected); Assert.Null(result.Error); Assert.Empty(result.Data!.Items); Assert.Equal(0, result.Data.Boundary); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("grant")][InlineData("profileCollection")][InlineData("session")]
    public async Task GivenRevocationBetweenPages_WhenContinuing_ThenReapplyCurrentCollectionPermission(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var scoped = new ProtectionFixture();
        var foreign = await ProfileSetup.Create(fixture, owner); var s = await ProfileSetup.Create(fixture, recipient);
        var first = await Collection(foreign); var second = await Collection(foreign);
        await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, first); var grant = await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, second);
        var p = await Profile(s, recipient, scoped, [first.PublicId, second.PublicId]); var selected = s with { Verifier = await Selected(s, p.Id) };
        var page = (await List(selected, 1)).Data!; Assert.True(page.HasMore);
        await using (var db = fixture.CreateContext()) {
            if (kind == "grant") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked));
            if (kind == "profileCollection") await db.ProfileCollections.Where(x => x.ProfileId == p.Id && x.CollectionId == second.Id).ExecuteDeleteAsync();
            if (kind == "session") await db.VaultAccessSessions.Where(x => x.HandleVerifier == selected.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true));
        }
        var before = await Snapshot(s); var result = await List(selected, 1, first.ServerSequence, page.Boundary);
        if (kind == "session") { Assert.Equal("vault_access_denied", result.Error); Assert.Null(result.Data); }
        else { Assert.Null(result.Error); Assert.Empty(result.Data!.Items); Assert.False(result.Data.HasMore); Assert.Equal(page.Boundary, result.Data.Boundary); }
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenCorruptNativeEvidenceBeyondPage_WhenListing_ThenRejectWithoutDisclosingOwnedPage()
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var foreign = await ProfileSetup.Create(fixture, owner); var s = await ProfileSetup.Create(fixture, recipient);
        await Collection(s); var shared = await Collection(foreign); var grant = await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, shared);
        await using (var db = fixture.CreateContext()) await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.RecipientKeyEnvelope, "bad"u8.ToArray()));
        var result = await List(s, 1); Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data);
    }

    [FunctionalTheory][InlineData("ownerRevision")][InlineData("ownerEpoch")][InlineData("ownerGeneration")][InlineData("recipientRevision")][InlineData("recipientEpoch")][InlineData("recipientGeneration")][InlineData("ownerKey")][InlineData("recipientKey")]
    public async Task GivenStaleCurrentProtectionPins_WhenListing_ThenRejectNativeGrant(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var replacement = new ProtectionFixture();
        var foreign = await ProfileSetup.Create(fixture, owner); var s = await ProfileSetup.Create(fixture, recipient); var c = await Collection(foreign); await AssociationSetup.Grant(fixture, foreign, owner, s, recipient, c);
        await using (var db = fixture.CreateContext()) {
            var pins = await db.VaultProtections.SingleAsync(x => x.AccountId == (kind.StartsWith("owner", StringComparison.Ordinal) ? foreign.InternalId : s.InternalId));
            if (kind.EndsWith("Revision", StringComparison.Ordinal)) pins.Revision = 0;
            if (kind.EndsWith("Epoch", StringComparison.Ordinal)) pins.KeyEpoch++;
            if (kind.EndsWith("Generation", StringComparison.Ordinal)) pins.RecoveryGeneration++;
            if (kind.EndsWith("Key", StringComparison.Ordinal)) pins.Material = Bytes(replacement.Material);
            await db.SaveChangesAsync();
        }
        var result = await List(s); Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data);
    }

    [FunctionalFact]
    public async Task GivenUnavailableCollectionTable_WhenListing_Then503WithoutMutation()
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); await Collection(s); var before = await Snapshot(s);
        await using var db = fixture.CreateContext(); await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection RENAME TO uc30_unavailable_collection");
        try { var result = await List(s); Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data); }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.uc30_unavailable_collection RENAME TO collection"); }
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenCancelledCaller_WhenListing_ThenPropagateCancellation()
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().ListAsync(new(s.Actor, s.Verifier, 10, 0, null), ct.Token));
    }

    [FunctionalTheory][InlineData("actor", "authentication_required")][InlineData("size", "validation_failed")][InlineData("afterNegative", "validation_failed")][InlineData("afterUnsafe", "validation_failed")][InlineData("boundaryNegative", "validation_failed")][InlineData("boundaryUnsafe", "validation_failed")]
    public async Task GivenInvalidStoreRequest_WhenListing_ThenRejectBeforeOpeningPersistence(string kind, string error)
    {
        var result = await new CollectionListStore(null!).ListAsync(new(kind == "actor" ? Guid.Empty : Guid.NewGuid(), new string('a', 64), kind == "size" ? 0 : 1,
            kind == "afterNegative" ? -1 : kind == "afterUnsafe" ? ProtocolBinary.MaxInteger + 1 : 0,
            kind == "boundaryNegative" ? -1 : kind == "boundaryUnsafe" ? ProtocolBinary.MaxInteger + 1 : null), default);
        Assert.Equal(error, result.Error); Assert.Null(result.Data);
    }

    [FunctionalFact]
    public async Task GivenCallerCancellationDuringRead_WhenListing_ThenPropagateCancellation()
    {
        using var owner = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, owner); using var ct = new CancellationTokenSource();
        var store = new CollectionListStore(new ProfileSetup.Factory(fixture, new CancelRead(ct)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListAsync(new(s.Actor, s.Verifier, 10, 0, null), ct.Token));
    }
    private sealed class CancelRead(CancellationTokenSource source) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { source.Cancel(); ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(result); }
    }

    private CollectionListStore Store()=>new(fixture);
    private Task<VaultResult<CollectionListPage>> List(ProfileSetup.State s,int size=100,long after=0,long? boundary=null)=>Store().ListAsync(new(s.Actor,s.Verifier,size,after,boundary),default);
    private async Task<VaultCollection> Collection(ProfileSetup.State s){var row=new VaultCollection{PublicId=Guid.NewGuid(),AccountId=s.InternalId,Envelope=Bytes(ProfileSetup.Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.CreateContext();db.Collections.Add(row);await db.SaveChangesAsync();return row;}
    private async Task<Profile> Profile(ProfileSetup.State s,ProtectionFixture owner,ProtectionFixture scoped,Guid[]? direct=null,Guid[]? collections=null){var input=ProfileSetup.Input(s,owner,scoped) with{RecordIds=[],FolderIds=[],CollectionIds=(direct??[]).Concat(collections??[]).Distinct().ToArray()};Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);await using var db=fixture.CreateContext();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==input.ProfileId);}
    private async Task<string> Selected(ProfileSetup.State s,long profile){var verifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));await using var db=fixture.CreateContext();db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,ProfileId=profile,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return verifier;}
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private async Task<string> Snapshot(ProfileSetup.State s){await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),ProfileRecords=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync(),ProfileFolders=await db.ProfileFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileCollections=await db.ProfileCollections.AsNoTracking().Where(x=>db.Profiles.Any(p=>p.Id==x.ProfileId && p.AccountId==s.InternalId)).OrderBy(x=>x.ProfileId).ThenBy(x=>x.CollectionId).ToArrayAsync(),CollectionRecords=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),CollectionFolders=await db.CollectionFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.FolderId).ToArrayAsync(),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>x.RecipientAccountId==s.InternalId || db.Collections.Any(c=>c.Id==x.CollectionId && c.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync()});}
    private sealed class RevokeAfterRead(PostgresFixture fixture,long grant):DbCommandInterceptor
    {public int Readers{get;private set;}public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData e,DbDataReader reader,CancellationToken ct=default){Readers++;if(Readers==1){await using var db=fixture.CreateContext();await db.CollectionGrants.Where(x=>x.Id==grant).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Revoked),ct);}return reader;}}
    private sealed class ExpireBeforeRead(DateTimeOffset deadline):DbCommandInterceptor
    {public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> result,CancellationToken ct=default){var wait=deadline-DateTimeOffset.UtcNow;if(wait>TimeSpan.Zero)await Task.Delay(wait+TimeSpan.FromMilliseconds(100),ct);return result;}}
}
