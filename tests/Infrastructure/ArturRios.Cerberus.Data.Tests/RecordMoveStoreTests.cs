using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
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
public class RecordMoveStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory]
    [InlineData("between")][InlineData("fromRoot")][InlineData("toRoot")][InlineData("same")][InlineData("rootNoop")]
    public async Task GivenOwnedCurrentRecord_WhenMoving_ThenPreserveIdentityCipherAndLinksAndBumpOnlyChangedImmediateFolders(string route)
    {
        // Given: real owned parents, record and unrelated records/collections.
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);
        var a=await AssociationSetup.Items(fixture,s);var b=await AssociationSetup.Items(fixture,s);var target=await Record(s,route is "fromRoot" or "rootNoop"?null:a.Folder.Id);
        await Members(s,a.Collection,[target.Id],[]);await Profile(s,owner,scoped,records:[target.PublicId]);
        var destination=route is "toRoot" or "rootNoop"?null:route=="same"?a.Folder:b.Folder;
        var stable=await Stable(s,target.Id,route is "same" or "rootNoop"?[]:new[]{a.Folder.Id,b.Folder.Id});
        // When: perform the optimistic parent replacement.
        var result=await Move(s,target,destination?.PublicId);
        // Then: only the intended record and changed immediate parents advance.
        await Success(target,destination,a.Folder,b.Folder,result);
        Assert.Equal(stable,await Stable(s,target.Id,route is "same" or "rootNoop"?[]:new[]{a.Folder.Id,b.Folder.Id}));
    }

    [FunctionalTheory][InlineData("direct")][InlineData("folder")][InlineData("collection")]
    public async Task GivenSelectedOwnerAndExistingEffectiveScope_WhenMovingWithinIt_ThenRetainValidSelectedHandle(string route)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var root=await Folder(s);var from=await Folder(s,root.Id);var to=await Folder(s,root.Id);var target=await Record(s,from.Id);var a=await AssociationSetup.Items(fixture,s);
        if(route=="collection")await Members(s,a.Collection,[],[root.Id]);
        var p=await Profile(s,owner,scoped,records:route=="direct"?[target.PublicId]:[],folders:route=="collection"?[]:[root.PublicId],collections:route=="collection"?[a.Collection.PublicId]:[]);s=s with{Verifier=await Selected(s,p.Id)};
        await Success(target,to,from,to,await Move(s,target,to.PublicId));
        Assert.Null((await new RecordReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,target.PublicId),default)).Error);
        await using var db=fixture.CreateContext();Assert.False((await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier)).Revoked);Assert.Equal(p.KeyWrappers,(await db.Profiles.SingleAsync(x=>x.Id==p.Id)).KeyWrappers);
    }

    [FunctionalTheory][InlineData("newProfile",false)][InlineData("newCollection",false)][InlineData("hiddenFolder",false)][InlineData("existingProfile",true)][InlineData("existingCollection",true)][InlineData("remove",true)][InlineData("accountWide",true)]
    public async Task GivenPossibleInheritedScopeExpansion_WhenMovingWithSelectedAccess_ThenOnlyRetainOrNarrowExistingContentScope(string kind,bool allowed)
    {
        using var owner=new ProtectionFixture();using var selectedKey=new ProtectionFixture();using var otherKey=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);var b=await AssociationSetup.Items(fixture,s);var target=await Record(s,a.Folder.Id);
        var p=await Profile(s,owner,selectedKey,records:[target.PublicId],folders:kind=="hiddenFolder"?[]:[a.Folder.PublicId,b.Folder.PublicId]);
        if(kind is "newProfile" or "existingProfile" or "accountWide")await Profile(s,owner,otherKey,records:kind=="existingProfile"?[target.PublicId]:[],folders:[b.Folder.PublicId]);
        if(kind is "newCollection" or "existingCollection")await Members(s,a.Collection,kind=="existingCollection"?[target.Id]:[],[b.Folder.Id]);
        if(kind!="accountWide")s=s with{Verifier=await Selected(s,p.Id)};
        var before=await Snapshot(s);var r=await Move(s,target,kind=="remove"?null:b.Folder.PublicId);
        if(allowed){await Success(target,kind=="remove"?null:b.Folder,a.Folder,b.Folder,r);if(kind=="remove")Assert.Null((await new RecordReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,target.PublicId),default)).Error);}
        else{Assert.Equal("not_found",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));}
    }

    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly,false)][InlineData(CollectionGrantAccess.ReadWrite,false)][InlineData(CollectionGrantAccess.ReadOnly,true)][InlineData(CollectionGrantAccess.ReadWrite,true)]
    public async Task GivenNativeRecipientsWithOldNewAndDirectRoutes_WhenOwnerMoves_ThenDynamicallyRecalculateGetAndListWithoutChangingKeys(CollectionGrantAccess access,bool selected)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);var b=await AssociationSetup.Items(fixture,s);var direct=await AssociationSetup.Items(fixture,s);var target=await Record(s,a.Folder.Id);
        await Members(s,a.Collection,[],[a.Folder.Id]);await Members(s,b.Collection,[],[b.Folder.Id]);await Members(s,direct.Collection,[target.Id],[]);
        var ga=await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection,access);var gb=await AssociationSetup.Grant(fixture,s,owner,recipient,client,b.Collection,access);var gd=await AssociationSetup.Grant(fixture,s,owner,recipient,client,direct.Collection,access);
        var recipientWide=recipient;
        if(selected){var p=await Profile(recipient,client,scoped,collections:[a.Collection.PublicId,b.Collection.PublicId,direct.Collection.PublicId]);recipient=recipient with{Verifier=await Selected(recipient,p.Id)};}
        var recipientBefore=await Snapshot(recipient);
        var before=(await new RecordReadStore(fixture).ReadAsync(new(recipient.Actor,recipient.Verifier,target.PublicId),default)).Data!;Assert.Contains(a.Collection.PublicId,before.CollectionIds);Assert.DoesNotContain(b.Collection.PublicId,before.CollectionIds);
        await Success(target,b.Folder,a.Folder,b.Folder,await Move(s,target,b.Folder.PublicId));
        var after=(await new RecordReadStore(fixture).ReadAsync(new(recipient.Actor,recipient.Verifier,target.PublicId),default)).Data!;Assert.DoesNotContain(a.Collection.PublicId,after.CollectionIds);Assert.Contains(b.Collection.PublicId,after.CollectionIds);Assert.Contains(direct.Collection.PublicId,after.CollectionIds);
        Assert.Contains((await new RecordListStore(fixture).ListAsync(new(recipient.Actor,recipient.Verifier,50,0,null),default)).Data!.Items,x=>x.RecordId==target.PublicId);
        await using(var db=fixture.CreateContext()){Assert.Equal(ga.RecipientKeyEnvelope,(await db.CollectionGrants.SingleAsync(x=>x.Id==ga.Id)).RecipientKeyEnvelope);Assert.Equal(gb.RecipientKeyEnvelope,(await db.CollectionGrants.SingleAsync(x=>x.Id==gb.Id)).RecipientKeyEnvelope);Assert.Equal(gd.RecipientKeyEnvelope,(await db.CollectionGrants.SingleAsync(x=>x.Id==gd.Id)).RecipientKeyEnvelope);}
        Assert.Equal(recipientBefore,await Snapshot(recipient));
        await using(var db=fixture.CreateContext())await db.CollectionRecords.Where(x=>x.RecordId==target.Id && x.CollectionId==direct.Collection.Id).ExecuteDeleteAsync();
        // Only old-folder access is now removed; retain a separate old-only recipient handle/profile.
        using var oldScoped=new ProtectionFixture();var oldProfile=await Profile(recipientWide,client,oldScoped,collections:[a.Collection.PublicId]);var oldHandle=await Selected(recipient,oldProfile.Id);
        Assert.Equal("not_found",(await new RecordReadStore(fixture).ReadAsync(new(recipient.Actor,oldHandle,target.PublicId),default)).Error);
        Assert.Empty((await new RecordListStore(fixture).ListAsync(new(recipient.Actor,oldHandle,50,0,null),default)).Data!.Items);
    }

    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly,false,false)][InlineData(CollectionGrantAccess.ReadWrite,false,false)][InlineData(CollectionGrantAccess.ReadOnly,true,true)][InlineData(CollectionGrantAccess.ReadWrite,true,true)]
    public async Task GivenVisibleNativeForeignTarget_WhenMovingWithStaleRevisionBadCipherAndHiddenDestination_Then403BeforeProbing(CollectionGrantAccess access,bool selected,bool inherited)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var target=await Record(o,inherited?a.Folder.Id:null);await Members(o,a.Collection,inherited?[]:[target.Id],inherited?[a.Folder.Id]:[]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,access);
        if(selected){var p=await Profile(s,client,scoped,collections:[a.Collection.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};}
        await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,"damaged"u8.ToArray()));
        var before=await Snapshot(o);Assert.Equal("vault_access_denied",(await Move(s,target,Guid.NewGuid(),99)).Error);Assert.Equal(before,await Snapshot(o));
    }

    [FunctionalTheory][InlineData("missing")][InlineData("foreign")][InlineData("unselected")][InlineData("trash")][InlineData("recordTerminal")][InlineData("ancestorTrash")][InlineData("ancestorTerminal")][InlineData("ownerClosing")][InlineData("ownerTerminal")][InlineData("grantRevoked")][InlineData("collectionTrash")][InlineData("removedMembership")]
    public async Task GivenHiddenSourceWithBadNativeAndStaleRevision_WhenMoving_Then404BeforeDisclosure(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=kind=="unselected"?o:await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var target=await Record(o,a.Folder.Id);await Members(o,a.Collection,[target.Id],[]);CollectionGrant? g=null;if(kind!="foreign" && s.InternalId!=o.InternalId)g=await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);if(kind=="unselected"){var p=await Profile(s,owner,scoped);s=s with{Verifier=await Selected(s,p.Id)};}
        await using(var db=fixture.CreateContext()){
            await db.Records.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,"bad"u8.ToArray()));if(g is not null)await db.CollectionGrants.Where(x=>x.Id==g.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.RecipientKeyEnvelope,"bad"u8.ToArray()));
            if(kind=="trash")await db.Records.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));if(kind=="ancestorTrash")await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));
            if(kind.EndsWith("Terminal")){var id=kind=="recordTerminal"?target.PublicId:kind=="ancestorTerminal"?a.Folder.PublicId:o.AccountId;db.TerminalErasures.Add(new(){ResourceId=id,ResourceKind=kind=="recordTerminal"?"record":kind=="ancestorTerminal"?"folder":"account",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
            if(kind=="ownerClosing")await db.Accounts.Where(x=>x.Id==o.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.State,AccountState.ClosurePending));if(kind=="grantRevoked")await db.CollectionGrants.Where(x=>x.Id==g!.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.State,CollectionGrantState.Revoked));if(kind=="collectionTrash")await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));if(kind=="removedMembership")await db.CollectionRecords.Where(x=>x.RecordId==target.Id).ExecuteDeleteAsync();
        }
        var before=await Snapshot(o);Assert.Equal("not_found",(await Store().MoveAsync(new(s.Actor,s.Verifier,kind=="missing"?Guid.NewGuid():target.PublicId,99,null),default)).Error);Assert.Equal(before,await Snapshot(o));
    }

    [FunctionalTheory][InlineData("missing","not_found")][InlineData("closing","not_found")][InlineData("terminal","not_found")][InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")][InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")][InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")][InlineData("nullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)][InlineData("selection","vault_access_denied")]
    public async Task GivenInvalidCurrentAuthority_WhenMoving_ThenFailClosed(string kind,string? error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);
        await using(var db=fixture.CreateContext()){var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);if(kind=="closing")a.State=AccountState.ClosurePending;if(kind=="terminal")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="zeroPolicy")v.PolicyRevision=0;if(kind=="zeroGeneration")v.RevocationGeneration=0;if(kind is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(kind is "nullExpiry" or "noExpiry")v.ExpiresAt=null;if(kind=="selection")v.ProfileId=long.MaxValue;await db.SaveChangesAsync();}
        var before=await Snapshot(s);var r=await Store().MoveAsync(new(kind=="missing"?Guid.NewGuid():s.Actor,kind=="access"?new string('a',64):s.Verifier,target.PublicId,1,null),default);Assert.Equal(error,r.Error);if(error is null)await Success(target,null,null,null,r);else Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("actor","authentication_required")][InlineData("id","validation_failed")][InlineData("destination","validation_failed")][InlineData("zero","validation_failed")][InlineData("unsafe","validation_failed")]
    public async Task GivenInvalidInternalRequest_WhenMoving_ThenRejectWithoutWrite(string kind,string error)
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);var before=await Snapshot(s);var r=await Store().MoveAsync(new(kind=="actor"?Guid.Empty:s.Actor,s.Verifier,kind=="id"?Guid.Empty:target.PublicId,kind=="zero"?0:kind=="unsafe"?ProtocolBinary.MaxInteger+1:1,kind=="destination"?Guid.Empty:null),default);Assert.Equal(error,r.Error);Assert.Equal(before,await Snapshot(s));}

    [FunctionalTheory][InlineData("missing","not_found")][InlineData("foreign","not_found")][InlineData("trash","not_found")][InlineData("ancestorTrash","not_found")][InlineData("terminal","not_found")][InlineData("ancestorTerminal","not_found")][InlineData("cycle","persistence_unavailable")][InlineData("hiddenCycle","not_found")]
    public async Task GivenMissingHiddenOrCyclicDestination_WhenMovingWithStaleRevision_ThenCurrentDestinationStateWins(string kind,string error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var root=await Folder(s);var to=await Folder(s,root.Id);var target=await Record(s);
        if(kind=="foreign"){var other=await ProfileSetup.Create(fixture,owner);to=await Folder(other);}
        await using(var db=fixture.CreateContext()){
            if(kind is "trash" or "ancestorTrash" or "hiddenCycle")await db.Folders.Where(x=>x.Id==(kind=="trash"?to.Id:root.Id)).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));
            if(kind is "terminal" or "ancestorTerminal"){db.TerminalErasures.Add(new(){ResourceId=kind=="terminal"?to.PublicId:root.PublicId,ResourceKind="folder",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
            if(kind is "cycle" or "hiddenCycle")await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ParentFolderId,to.Id));
        }
        var before=await Snapshot(s);Assert.Equal(error,(await Move(s,target,kind=="missing"?Guid.NewGuid():to.PublicId,99)).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("cipher","persistence_unavailable")][InlineData("zeroRevision","persistence_unavailable")][InlineData("zeroSequence","persistence_unavailable")][InlineData("unsafeSequence","persistence_unavailable")][InlineData("purge","persistence_unavailable")][InlineData("maxRevision","revision_conflict")][InlineData("stale","revision_conflict")]
    public async Task GivenCorruptOrConflictingVisibleTarget_WhenMoving_ThenPreserveEveryRow(string kind,string error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);var to=await Folder(s);
        await using(var db=fixture.CreateContext()){var row=await db.Records.SingleAsync(x=>x.Id==target.Id);if(kind=="cipher")row.Envelope="bad"u8.ToArray();if(kind=="zeroRevision")row.Revision=0;if(kind=="maxRevision")row.Revision=ProtocolBinary.MaxInteger;if(kind=="zeroSequence")row.ServerSequence=0;if(kind=="unsafeSequence")row.ServerSequence=ProtocolBinary.MaxInteger+1;if(kind=="purge")row.PurgeAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();}
        var before=await Snapshot(s);Assert.Equal(error,(await Move(s,target,to.PublicId,kind=="stale"?99:kind=="maxRevision"?ProtocolBinary.MaxInteger:1)).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData(false,"zeroRevision","persistence_unavailable")][InlineData(true,"zeroRevision","persistence_unavailable")][InlineData(false,"zeroSequence","persistence_unavailable")][InlineData(true,"zeroSequence","persistence_unavailable")][InlineData(false,"maxRevision","revision_conflict")][InlineData(true,"maxRevision","revision_conflict")]
    public async Task GivenInvalidChangedImmediateParentMetadata_WhenMoving_ThenNoPartialRecordOrFolderUpdate(bool destination,string kind,string error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var from=await Folder(s);var to=await Folder(s);var target=await Record(s,from.Id);
        await using(var db=fixture.CreateContext()){var row=await db.Folders.SingleAsync(x=>x.Id==(destination?to.Id:from.Id));if(kind=="zeroRevision")row.Revision=0;if(kind=="zeroSequence")row.ServerSequence=0;if(kind=="maxRevision")row.Revision=ProtocolBinary.MaxInteger;await db.SaveChangesAsync();}
        var before=await Snapshot(s);Assert.Equal(error,(await Move(s,target,to.PublicId)).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenSameParentAtMaximumRevision_WhenNoopMoving_ThenAdvanceOnlyRecordAndRetryOldRevisionConflicts()
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var to=await Folder(s);await using(var db=fixture.CreateContext())await db.Folders.Where(x=>x.Id==to.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revision,ProtocolBinary.MaxInteger));var beforeParent=await FolderRow(to.Id);var target=await Record(s,to.Id);
        await Success(target,to,beforeParent,beforeParent,await Move(s,target,to.PublicId));var before=await Snapshot(s);Assert.Equal("revision_conflict",(await Move(s,target,to.PublicId)).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("signature")][InlineData("grantRevision")][InlineData("recipientPins")][InlineData("ownerPins")][InlineData("collectionEpoch")][InlineData("quotedEpoch")][InlineData("unknown")][InlineData("duplicate")][InlineData("wrongIdentity")]
    public async Task GivenMalformedRequiredResultingNativeEnvelope_WhenMovingIntoCollectionFolder_Then503NoMutation(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var c=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);var target=await Record(s);await Members(s,a.Collection,[],[a.Folder.Id]);var g=await AssociationSetup.Grant(fixture,s,owner,c,client,a.Collection,CollectionGrantAccess.ReadWrite);
        await using(var db=fixture.CreateContext()){
            if(kind=="grantRevision")await db.CollectionGrants.Where(x=>x.Id==g.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revision,2));
            if(kind is "signature" or "unknown" or "duplicate" or "wrongIdentity"){var raw=JsonSerializer.Deserialize<RecipientEnvelope>(g.RecipientKeyEnvelope,ProtectionFixture.Json)!;var bytes=kind=="signature"?Bytes(raw with{Signature=ProtocolBinary.Encode(new byte[64])}):kind=="wrongIdentity"?Bytes(raw with{RecipientIdentityId=Guid.NewGuid()}):Malformed(g.RecipientKeyEnvelope,kind);await db.CollectionGrants.Where(x=>x.Id==g.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.RecipientKeyEnvelope,bytes));}
            if(kind is "recipientPins" or "ownerPins")await db.VaultProtections.Where(x=>x.AccountId==(kind=="recipientPins"?c.InternalId:s.InternalId)).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Material,"bad"u8.ToArray()));
            if(kind=="collectionEpoch")await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.KeyEpoch,2));if(kind=="quotedEpoch")await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,Malformed(a.Collection.Envelope,"numericString")));
        }
        var before=await Snapshot(s);var recipient=await Snapshot(c);Assert.Equal("persistence_unavailable",(await Move(s,target,a.Folder.PublicId)).Error);Assert.Equal(before,await Snapshot(s));Assert.Equal(recipient,await Snapshot(c));
    }

    [FunctionalFact]
    public async Task GivenMalformedOldOnlyNativeRoute_WhenMovingOutOfIt_ThenRetainNoInvalidResultingEnvelopeAndRemoveAccess()
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var c=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);var target=await Record(s,a.Folder.Id);await Members(s,a.Collection,[],[a.Folder.Id]);var g=await AssociationSetup.Grant(fixture,s,owner,c,client,a.Collection);
        await using(var db=fixture.CreateContext())await db.CollectionGrants.Where(x=>x.Id==g.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.RecipientKeyEnvelope,"bad"u8.ToArray()));await Success(target,null,a.Folder,null,await Move(s,target,null));Assert.Equal("not_found",(await new RecordReadStore(fixture).ReadAsync(new(c.Actor,c.Verifier,target.PublicId),default)).Error);
    }

    [FunctionalTheory][InlineData("afterRecord")][InlineData("firstParent")][InlineData("secondParent")]
    public async Task GivenPersistenceFailureAfterPartialMove_WhenRetrying_ThenAllRecordAndParentChangesRollBack(string stage)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var from=await Folder(s);var to=await Folder(s);var target=await Record(s,from.Id);var before=await Snapshot(s);var store=new RecordMoveStore(new ProfileSetup.Factory(fixture,new Fail(stage)));
        Assert.Equal("persistence_unavailable",(await store.MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,to.PublicId),default)).Error);Assert.Equal(before,await Snapshot(s));await Success(target,to,from,to,await Move(s,target,to.PublicId));
    }

    [FunctionalTheory][InlineData("expiry","vault_access_denied")][InlineData("sourceHidden","not_found")][InlineData("destinationHidden","not_found")][InlineData("terminal","not_found")][InlineData("recipientPins","persistence_unavailable")]
    public async Task GivenStateChangeImmediatelyBeforeGuardedMove_WhenWriting_ThenReevaluateCurrentAuthorityAndNativeEvidence(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var nextClient=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var c=await ProfileSetup.Create(fixture,client);var sourceRoot=await Folder(s);var from=await Folder(s,sourceRoot.Id);var destRoot=await Folder(s);var to=await Folder(s,destRoot.Id);var target=await Record(s,from.Id);var a=await AssociationSetup.Items(fixture,s);await Members(s,a.Collection,[],[to.Id]);await AssociationSetup.Grant(fixture,s,owner,c,client,a.Collection);
        var before=await Snapshot(s);var hook=new BeforeWrite(async()=>{await using var db=fixture.CreateContext();if(kind=="expiry")await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,DateTimeOffset.UtcNow.AddMinutes(-1)));if(kind is "sourceHidden" or "destinationHidden")await db.Folders.Where(x=>x.Id==(kind=="sourceHidden"?sourceRoot.Id:destRoot.Id)).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));if(kind=="terminal"){db.TerminalErasures.Add(new(){ResourceKind="record",ResourceId=target.PublicId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}if(kind=="recipientPins")await db.VaultProtections.Where(x=>x.AccountId==c.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Material,Bytes(nextClient.Material)));});
        // Expiry changes our locked session: use its transaction command connection instead (see hook).
        if(kind=="expiry")hook=new BeforeWrite(async command=>{await using var cmd=command.Connection!.CreateCommand();cmd.Transaction=command.Transaction;cmd.CommandText="UPDATE cerberus.vault_access_session SET expires_at=statement_timestamp()-interval '1 second' WHERE handle_verifier=@v";cmd.Parameters.Add(new NpgsqlParameter("v",s.Verifier));await cmd.ExecuteNonQueryAsync();});
        var store=new RecordMoveStore(new ProfileSetup.Factory(fixture,hook));Assert.Equal(error,(await store.MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,to.PublicId),default)).Error);
        // Our own failed transaction rolls back its expiry injection; external authority changes survive.
        Assert.Equal(Bytes(target),Bytes(await Row(target.Id)));Assert.Equal(Bytes(from),Bytes(await FolderRow(from.Id)));Assert.Equal(Bytes(to),Bytes(await FolderRow(to.Id)));if(kind=="expiry")Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenCanceledCallerOrUnavailableFactory_WhenMoving_ThenPropagateCancellationOrSafe503()
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);var before=await Snapshot(s);using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,null),cancel.Token));Assert.Equal(before,await Snapshot(s));
        var factory=new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1").Options);Assert.Equal("persistence_unavailable",(await new RecordMoveStore(factory).MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,null),default)).Error);
    }


    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenControlledNativeRecipientEditAndOwnerMove_WhenOneHoldsEarlierCollectionLocks_ThenOneWinnerWithoutDeadlock(bool moveFirst)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var c=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var to=await Folder(o);var target=await Record(o,a.Folder.Id);await Members(o,a.Collection,[],[a.Folder.Id]);await AssociationSetup.Grant(fixture,o,owner,c,client,a.Collection,CollectionGrantAccess.ReadWrite);
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<VaultResult<RecordMoveDetails>> move;Task<VaultResult<RecordCreateDetails>> edit;var input=new RecordUpdateInput(1,ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch.AddDays(1));
        if(moveFirst){move=new RecordMoveStore(new ProfileSetup.Factory(fixture,new BeforeWrite(async()=>{reached.TrySetResult();await release.Task.WaitAsync(ct.Token);}))).MoveAsync(new(o.Actor,o.Verifier,target.PublicId,1,to.PublicId),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));edit=new RecordUpdateStore(new ProfileSetup.Factory(fixture,new Signal(blocked,"collection"))).UpdateAsync(new(c.Actor,c.Verifier,target.PublicId,input),ct.Token);}
        else{edit=new RecordUpdateStore(new ProfileSetup.Factory(fixture,new BeforeWrite(async()=>{reached.TrySetResult();await release.Task.WaitAsync(ct.Token);}))).UpdateAsync(new(c.Actor,c.Verifier,target.PublicId,input),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));move=new RecordMoveStore(new ProfileSetup.Factory(fixture,new Signal(blocked,"collection"))).MoveAsync(new(o.Actor,o.Verifier,target.PublicId,1,to.PublicId),ct.Token);}
        try{await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(moveFirst?edit.IsCompleted:move.IsCompleted);}finally{release.TrySetResult();}
        await Task.WhenAll(move,edit);if(moveFirst){await Success(target,to,a.Folder,to,move.Result);Assert.Equal("not_found",edit.Result.Error);}else{Assert.Null(edit.Result.Error);Assert.Equal("revision_conflict",move.Result.Error);var row=await Row(target.Id);Assert.Equal(a.Folder.Id,row.FolderId);Assert.Equal(Bytes(input.Envelope),row.Envelope);Assert.Equal(Bytes(a.Folder),Bytes(await FolderRow(a.Folder.Id)));Assert.Equal(Bytes(to),Bytes(await FolderRow(to.Id)));}
    }

    [FunctionalTheory][InlineData("edit",false)][InlineData("edit",true)][InlineData("trash",false)][InlineData("trash",true)]
    public async Task GivenControlledActualOwnedWriterAndMove_WhenOneHoldsAccount_ThenPreserveTheExpectedRevisionWinner(string writer,bool moveFirst)
    {
        using var owner=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var from=await Folder(o);var to=await Folder(o);var target=await Record(o,from.Id);var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pause=new ProfileSetup.Factory(fixture,new BeforeWrite(async()=>{reached.TrySetResult();await release.Task.WaitAsync(ct.Token);}));var signal=new ProfileSetup.Factory(fixture,new Signal(blocked,"account"));
        Task<VaultResult<RecordMoveDetails>> move;Task<VaultResult<RecordCreateDetails>>? edit=null;Task<VaultResult<RecordTrashDetails>>? trash=null;
        void StartOther(IDbContextFactory<AppDbContext> factory){if(writer=="edit")edit=new RecordUpdateStore(factory).UpdateAsync(new(o.Actor,o.Verifier,target.PublicId,new(1,ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch)),ct.Token);else trash=new RecordTrashStore(factory).TrashAsync(new(o.Actor,o.Verifier,target.PublicId,1),ct.Token);}
        if(moveFirst){move=new RecordMoveStore(pause).MoveAsync(new(o.Actor,o.Verifier,target.PublicId,1,to.PublicId),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));StartOther(signal);}else{StartOther(pause);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));move=new RecordMoveStore(signal).MoveAsync(new(o.Actor,o.Verifier,target.PublicId,1,to.PublicId),ct.Token);}
        try{await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(moveFirst?(edit?.IsCompleted??trash!.IsCompleted):move.IsCompleted);}finally{release.TrySetResult();}
        var mr=await move;var otherError=edit is not null?(await edit).Error:(await trash!).Error;
        if(moveFirst){await Success(target,to,from,to,mr);Assert.Equal("revision_conflict",otherError);}else{Assert.Null(otherError);Assert.Equal(writer=="trash"?"not_found":"revision_conflict",mr.Error);var row=await Row(target.Id);Assert.Equal(2,row.Revision);Assert.Equal(writer=="trash"?null:from.Id,row.FolderId);Assert.Equal(Bytes(to),Bytes(await FolderRow(to.Id)));if(writer=="trash")Assert.NotNull(row.DeletedAt);}
    }

    [FunctionalFact]
    public async Task GivenConcurrentMovesAndLostSuccessRetry_WhenReplacingParent_ThenOneStructuralChangeAndNoDuplicateBumps()
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var from=await Folder(s);var to=await Folder(s);var target=await Record(s,from.Id);var tasks=new[]{Move(s,target,to.PublicId),Move(s,target,to.PublicId)};var results=await Task.WhenAll(tasks);Assert.Single(results,x=>x.Error is null);Assert.Single(results,x=>x.Error=="revision_conflict");await Success(target,to,from,to,results.Single(x=>x.Error is null));var before=await Snapshot(s);Assert.Equal("revision_conflict",(await Move(s,target,to.PublicId)).Error);Assert.Equal(before,await Snapshot(s));
        var row=await Row(target.Id);var nowFrom=await FolderRow(from.Id);var nowTo=await FolderRow(to.Id);await Success(row,to,nowFrom,nowTo,await Move(s,row,to.PublicId,2));
    }

    [FunctionalTheory][InlineData("account")][InlineData("session")][InlineData("profile")][InlineData("collection")][InlineData("grant")][InlineData("folder")][InlineData("record")]
    public async Task GivenExpiryDuringEachRequiredLockWaitAndStaleRevision_WhenMoving_Then403BeforeConflict(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);var to=await Folder(s);var target=await Record(s,a.Folder.Id);await Members(s,a.Collection,[target.Id],[]);var g=await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);var p=await Profile(s,owner,scoped,records:[target.PublicId],folders:[a.Folder.PublicId,to.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};
        var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));var before=await Snapshot(s);
        await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();var firstFolder=new[]{a.Folder,to}.OrderBy(x=>x.PublicId).First();var id=kind switch{"account"=>s.InternalId,"profile"=>p.Id,"collection"=>a.Collection.Id,"grant"=>g.Id,"folder"=>firstFolder.Id,_=>target.Id};var table=kind switch{"session"=>"vault_access_session","grant"=>"collection_grant",_=>kind};var sql=kind=="session"?"SELECT FROM cerberus.vault_access_session WHERE handle_verifier={0} FOR UPDATE":"SELECT FROM cerberus."+table+" WHERE id={0} FOR UPDATE";await held.Database.ExecuteSqlRawAsync(sql,kind=="session"?(object)s.Verifier:id);
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));var pending=new RecordMoveStore(new ProfileSetup.Factory(fixture,new Signal(reached,table))).MoveAsync(new(s.Actor,s.Verifier,target.PublicId,99,to.PublicId),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);await Past(deadline);await tx.CommitAsync();Assert.Equal("vault_access_denied",(await pending).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("newCollection")][InlineData("newGrant")][InlineData("sourceParent")]
    public async Task GivenNewEarlierAuthorityAfterDiscovery_WhenMoving_Then409WithoutLateLocksOrLostExternalWinner(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var from=await Folder(s);var to=await Folder(s);var other=await Folder(s);var target=await Record(s,from.Id);var a=await AssociationSetup.Items(fixture,s);if(kind=="newGrant")await Members(s,a.Collection,[],[to.Id]);
        async Task Change(){if(kind=="newCollection")await Members(s,a.Collection,[],[to.Id]);else if(kind=="newGrant")await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);else{await using var db=fixture.CreateContext();await db.Records.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.FolderId,other.Id));}}
        DbCommandInterceptor hook=kind=="sourceParent"?new BeforeTarget(Change):new BeforeWrite(Change);var result=await new RecordMoveStore(new ProfileSetup.Factory(fixture,hook)).MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,to.PublicId),default);Assert.Equal("revision_conflict",result.Error);
        var row=await Row(target.Id);Assert.Equal(kind=="sourceParent"?other.Id:from.Id,row.FolderId);Assert.Equal(target.Envelope,row.Envelope);Assert.Equal(1,row.Revision);Assert.Equal(Bytes(from),Bytes(await FolderRow(from.Id)));Assert.Equal(Bytes(to),Bytes(await FolderRow(to.Id)));
    }

    [FunctionalTheory][InlineData("sourceHidden","not_found")][InlineData("destinationHidden","not_found")][InlineData("destinationCycle","persistence_unavailable")][InlineData("ownerTerminal","not_found")]
    public async Task GivenCompleteAncestryOrTerminalChangeBeforeWrite_WhenMoving_ThenCurrentRecursiveStatementDenies(string kind,string error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var sourceRoot=await Folder(s);var from=await Folder(s,sourceRoot.Id);var destinationRoot=await Folder(s);var to=await Folder(s,destinationRoot.Id);var hidden=await Folder(s);var target=await Record(s,from.Id);await using(var db=fixture.CreateContext())await db.Folders.Where(x=>x.Id==hidden.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));
        var hook=new BeforeWrite(async()=>{await using var db=fixture.CreateContext();if(kind=="ownerTerminal"){db.TerminalErasures.Add(new(){ResourceKind="account",ResourceId=s.AccountId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}else{var id=kind=="sourceHidden"?sourceRoot.Id:destinationRoot.Id;await db.Folders.Where(x=>x.Id==id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ParentFolderId,kind=="destinationCycle"?to.Id:hidden.Id));}});
        Assert.Equal(error,(await new RecordMoveStore(new ProfileSetup.Factory(fixture,hook)).MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,to.PublicId),default)).Error);Assert.Equal(Bytes(target),Bytes(await Row(target.Id)));Assert.Equal(Bytes(from),Bytes(await FolderRow(from.Id)));Assert.Equal(Bytes(to),Bytes(await FolderRow(to.Id)));
    }

    [FunctionalFact]
    public async Task GivenActualRecipientProtectionRotationAfterNativeValidation_WhenMoving_ThenStaleEvidenceConflictsAndFreshRetrySucceeds()
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);var target=await Record(s);await Members(s,a.Collection,[],[a.Folder.Id]);await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);
        var change=new ProtectionChange(recipient.AccountId,1,1,"rotate-content",client.Rewrap(),[new("account",recipient.AccountId,1,ProfileSetup.Envelope(2))]);var raw=Bytes(change);var challenge=(await new VaultProtectionStore(fixture).ChallengeAsync(recipient.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;
        var before=await Snapshot(s);var hook=new BeforeWrite(async()=>Assert.Null((await new VaultProtectionChangeStore(fixture).ChangeAsync(new(recipient.Actor,recipient.Verifier,challenge.ChallengeId,client.Sign(challenge),raw,change),default)).Error));
        Assert.Equal("revision_conflict",(await new RecordMoveStore(new ProfileSetup.Factory(fixture,hook)).MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,a.Folder.PublicId),default)).Error);Assert.Equal(before,await Snapshot(s));await Success(target,a.Folder,null,a.Folder,await Move(s,target,a.Folder.PublicId));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenActualOwnedCreateOrAssociationWaitsBehindMove_WhenCommitting_ThenNoFkCycleAndBothAuthorizedWritesComplete(bool association)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var from=await Folder(s);var to=await Folder(s);var target=await Record(s,from.Id);var p=await Profile(s,owner,scoped,records:[target.PublicId]);var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var move=new RecordMoveStore(new ProfileSetup.Factory(fixture,new BeforeWrite(async()=>{reached.TrySetResult();await release.Task.WaitAsync(ct.Token);}))).MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,to.PublicId),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));var factory=new ProfileSetup.Factory(fixture,new Signal(blocked,"account"));var input=new RecordCreateInput(Guid.NewGuid(),ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch,[],to.PublicId);var create=association?null:new RecordCreateStore(factory).CreateAsync(new(s.Actor,s.Verifier,input),ct.Token);var link=association?new ProfileAssociationStore(factory).SetAsync(new(s.Actor,s.Verifier,p.PublicId,new(p.Revision,[target.PublicId],[to.PublicId],[])),ct.Token):null;
        try{await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(create?.IsCompleted??link!.IsCompleted);}finally{release.TrySetResult();}Assert.Null((await move).Error);if(create is not null){Assert.Null((await create).Error);Assert.Equal(to.Revision+2,(await FolderRow(to.Id)).Revision);}if(link is not null){Assert.Null((await link).Error);await using var db=fixture.CreateContext();Assert.Equal(p.Revision+1,(await db.Profiles.SingleAsync(x=>x.Id==p.Id)).Revision);}
        Assert.Equal(to.Id,(await Row(target.Id)).FolderId);Assert.Equal(target.Envelope,(await Row(target.Id)).Envelope);Assert.Equal(from.Revision+1,(await FolderRow(from.Id)).Revision);
    }

    [FunctionalFact]
    public async Task GivenActualOwnerRotationWaitsBehindMove_WhenMoveCommits_ThenStaleInventoryConflictsWithoutConsumingProof()
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var from=await Folder(s);var to=await Folder(s);var target=await Record(s,from.Id);var change=new ProtectionChange(s.AccountId,1,1,"rotate-content",owner.Rewrap(),[new("account",s.AccountId,1,ProfileSetup.Envelope(2)),new("record",target.PublicId,1,ProfileSetup.Envelope(2)),new("folder",from.PublicId,1,ProfileSetup.Envelope(2)),new("folder",to.PublicId,1,ProfileSetup.Envelope(2))]);var raw=Bytes(change);var challenge=(await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));var move=new RecordMoveStore(new ProfileSetup.Factory(fixture,new BeforeWrite(async()=>{reached.TrySetResult();await release.Task.WaitAsync(ct.Token);}))).MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,to.PublicId),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));var rotation=new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture,new Signal(blocked,"account"))).ChangeAsync(new(s.Actor,s.Verifier,challenge.ChallengeId,owner.Sign(challenge),raw,change),ct.Token);
        try{await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(rotation.IsCompleted);}finally{release.TrySetResult();}await Success(target,to,from,to,await move);Assert.Equal("revision_conflict",(await rotation).Error);await using var db=fixture.CreateContext();Assert.False((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==challenge.ChallengeId)).Consumed);Assert.Equal(1,(await db.VaultProtections.SingleAsync(x=>x.AccountId==s.InternalId)).Revision);
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenActualOwnerRotationBeforeOrAfterMove_WhenReloading_ThenRetainCurrentParentAndRespectEpochRevisionAndHandle(bool rotateFirst)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var from=await Folder(s);var to=await Folder(s);var target=await Record(s,from.Id);
        if(!rotateFirst){await Success(target,to,from,to,await Move(s,target,to.PublicId));target=await Row(target.Id);from=await FolderRow(from.Id);to=await FolderRow(to.Id);}
        var change=new ProtectionChange(s.AccountId,1,1,"rotate-content",owner.Rewrap(),[new("account",s.AccountId,1,ProfileSetup.Envelope(2)),new("record",target.PublicId,target.Revision,ProfileSetup.Envelope(2)),new("folder",from.PublicId,from.Revision,ProfileSetup.Envelope(2)),new("folder",to.PublicId,to.Revision,ProfileSetup.Envelope(2))]);var raw=Bytes(change);var challenge=(await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;Assert.Null((await new VaultProtectionChangeStore(fixture).ChangeAsync(new(s.Actor,s.Verifier,challenge.ChallengeId,owner.Sign(challenge),raw,change),default)).Error);
        Assert.Equal("vault_access_denied",(await Move(s,target,to.PublicId,1)).Error);var row=await Row(target.Id);Assert.Equal(rotateFirst?from.Id:to.Id,row.FolderId);Assert.Equal(2,JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,ProtectionFixture.Json)!.KeyEpoch);
        if(rotateFirst){var verifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));await using(var db=fixture.CreateContext()){var account=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=account.PolicyRevision,RevocationGeneration=account.RevocationGeneration});await db.SaveChangesAsync();}s=s with{Verifier=verifier};var before=await Snapshot(s);Assert.Equal("revision_conflict",(await Move(s,row,to.PublicId,1)).Error);Assert.Equal(before,await Snapshot(s));await Success(row,to,await FolderRow(from.Id),await FolderRow(to.Id),await Move(s,row,to.PublicId,row.Revision));}
    }

    [FunctionalTheory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task GivenSequenceExhaustionOrRegressionAtTargetOrParent_WhenMoving_ThenRollbackAllStructuralChanges(bool parent,bool regression)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var from=await Folder(s);var to=await Folder(s);var target=await Record(s,from.Id);var before=await Snapshot(s);await using var db=fixture.CreateContext();var previous=await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();
        async Task Reset(){await using var other=fixture.CreateContext();await other.Database.ExecuteSqlRawAsync(regression?"SELECT setval('cerberus.server_sequence',1,false)":"SELECT setval('cerberus.server_sequence',9007199254740991,true)");}
        try{var store=parent?new RecordMoveStore(new ProfileSetup.Factory(fixture,new BeforeParent(Reset))):Store();if(!parent)await Reset();Assert.Equal(regression?"persistence_unavailable":"revision_conflict",(await store.MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,to.PublicId),default)).Error);Assert.Equal(before,await Snapshot(s));}finally{await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{previous},true)");}
    }

    [FunctionalTheory][InlineData("afterFirstParent")][InlineData("afterSecondParent")]
    public async Task GivenFailureAfterActualParentMutation_WhenMoving_ThenRollbackBothParentsAndRecord(string stage)
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var from=await Folder(s);var to=await Folder(s);var target=await Record(s,from.Id);var before=await Snapshot(s);Assert.Equal("persistence_unavailable",(await new RecordMoveStore(new ProfileSetup.Factory(fixture,new Fail(stage))).MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,to.PublicId),default)).Error);Assert.Equal(before,await Snapshot(s));await Success(target,to,from,to,await Move(s,target,to.PublicId));}

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenOverlappingRequiredNativeContributorsWithOneBadWrapper_WhenMovingOrForeignDenial_ThenWholeOperation503(bool foreign)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var c=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var b=await AssociationSetup.Items(fixture,o);var target=await Record(o);await Members(o,a.Collection,foreign?[target.Id]:[],foreign?[]:[a.Folder.Id]);await Members(o,b.Collection,foreign?[target.Id]:[],foreign?[]:[a.Folder.Id]);await AssociationSetup.Grant(fixture,o,owner,c,client,a.Collection,CollectionGrantAccess.ReadWrite);var bad=await AssociationSetup.Grant(fixture,o,owner,c,client,b.Collection,CollectionGrantAccess.ReadOnly);await using(var db=fixture.CreateContext())await db.CollectionGrants.Where(x=>x.Id==bad.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.RecipientKeyEnvelope,"bad"u8.ToArray()));var before=await Snapshot(o);Assert.Equal("persistence_unavailable",(await Move(foreign?c:o,target,a.Folder.PublicId)).Error);Assert.Equal(before,await Snapshot(o));
    }

    [FunctionalFact]
    public async Task GivenNativeVisibleForeignTarget_WhenDenyingMove_ThenOnlyOwnAccountAndSessionLocks()
    {using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var c=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);await Members(o,a.Collection,[a.Record.Id],[]);await AssociationSetup.Grant(fixture,o,owner,c,client,a.Collection,CollectionGrantAccess.ReadWrite);var capture=new Capture(fixture);Assert.Equal("vault_access_denied",(await new RecordMoveStore(new ProfileSetup.Factory(fixture,capture)).MoveAsync(new(c.Actor,c.Verifier,a.Record.PublicId,1,null),default)).Error);Assert.Equal(2,capture.Locks.Count);Assert.Contains("cerberus.account",capture.Locks[0]);Assert.Contains("cerberus.vault_access_session",capture.Locks[1]);}

    [FunctionalFact]
    public async Task GivenUnrelatedDeepInventoryAndTwoTargetPaths_WhenMoving_ThenAnalyzeOnlyOneSourceAndDestinationAndOrderOwnedLocks()
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var root=await Folder(s);var from=await Folder(s,root.Id);var root2=await Folder(s);var child2=await Folder(s,root2.Id);var to=await Folder(s,child2.Id);var target=await Record(s,from.Id);var a=await AssociationSetup.Items(fixture,s);await Members(s,a.Collection,[target.Id],[root2.Id]);await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);
        var unrelated=await Folder(s);for(var i=0;i<80;i++){unrelated=await Folder(s,unrelated.Id);await Record(s,unrelated.Id);}
        var capture=new Capture(fixture,true);await Success(target,to,from,to,await new RecordMoveStore(new ProfileSetup.Factory(fixture,capture)).MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,to.PublicId),default));Assert.NotNull(capture.Plan);using var plan=JsonDocument.Parse(capture.Plan);var nodes=Nodes(plan.RootElement).Where(x=>x.TryGetProperty("CTE Name",out _)).ToArray();foreach(var name in new[]{"candidates","destination_candidate"}){var scans=nodes.Where(x=>x.GetProperty("CTE Name").GetString()==name).ToArray();Assert.NotEmpty(scans);Assert.All(scans,x=>Assert.InRange(x.GetProperty("Actual Rows").GetDecimal(),0,1));Assert.Contains(scans,x=>x.GetProperty("Actual Rows").GetDecimal()==1);}
        var locks=capture.Locks;var collection=locks.FindIndex(x=>x.Contains("cerberus.collection c")&&x.Contains("FOR NO KEY UPDATE"));var grant=locks.FindIndex(x=>x.Contains("collection_grant g")&&x.Contains("FOR SHARE"));var folder=locks.FindIndex(x=>x.Contains("cerberus.folder f")&&x.Contains("FOR NO KEY UPDATE"));var record=locks.FindIndex(x=>x.Contains("cerberus.record WHERE")&&x.Contains("FOR NO KEY UPDATE"));Assert.True(collection>=0&&grant>collection&&folder>grant&&record>folder);Assert.Contains("ORDER BY c.public_id",locks[collection]);Assert.Contains("ORDER BY g.public_id",locks[grant]);Assert.Contains("ORDER BY f.public_id",locks[folder]);
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenCurrentSourceCycleWithDirectVisibility_WhenMoving_Then503OrHidden404BeforeStaleRevision(bool hidden)
    {using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var root=await Folder(s);var child=await Folder(s,root.Id);var target=await Record(s,child.Id);await Profile(s,owner,key,records:[target.PublicId]);await using(var db=fixture.CreateContext()){await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ParentFolderId,child.Id).SetProperty(v=>v.DeletedAt,hidden?(DateTimeOffset?)DateTimeOffset.UtcNow:null));}var before=await Snapshot(s);Assert.Equal(hidden?"not_found":"persistence_unavailable",(await Move(s,target,null,99)).Error);Assert.Equal(before,await Snapshot(s));}

    [FunctionalTheory][InlineData("foreign")][InlineData("trash")][InlineData("terminal")]
    public async Task GivenInvalidSelectedProfile_WhenMoving_Then403WithoutBroadening(string kind)
    {using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);var p=await Profile(kind=="foreign"?await ProfileSetup.Create(fixture,owner):s,owner,key);s=s with{Verifier=await Selected(s,p.Id)};await using(var db=fixture.CreateContext()){if(kind=="trash")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));if(kind=="terminal"){db.TerminalErasures.Add(new(){ResourceKind="profile",ResourceId=p.PublicId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}var before=await Snapshot(s);Assert.Equal("vault_access_denied",(await Move(s,target,null,99)).Error);Assert.Equal(before,await Snapshot(s));}

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenUnsafeImmediateParentSequence_WhenMoving_Then503WithNoPartialChanges(bool destination)
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var from=await Folder(s);var to=await Folder(s);var target=await Record(s,from.Id);await using(var db=fixture.CreateContext())await db.Folders.Where(x=>x.Id==(destination?to.Id:from.Id)).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ServerSequence,ProtocolBinary.MaxInteger+1));var before=await Snapshot(s);Assert.Equal("persistence_unavailable",(await Move(s,target,to.PublicId)).Error);Assert.Equal(before,await Snapshot(s));}

    [FunctionalFact]
    public async Task GivenExpiryAfterAllLocksBeforeFinalStatement_WhenMoving_Then403WithoutCommit()
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));var before=await Snapshot(s);var store=new RecordMoveStore(new ProfileSetup.Factory(fixture,new BeforeWrite(()=>Past(deadline))));Assert.Equal("vault_access_denied",(await store.MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,null),default)).Error);Assert.Equal(before,await Snapshot(s));}

    [FunctionalFact]
    public async Task GivenProviderInternalCancellationBeforeFinalStatement_WhenMoving_Then503WithoutMutation()
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);var before=await Snapshot(s);var store=new RecordMoveStore(new ProfileSetup.Factory(fixture,new BeforeWrite(()=>throw new OperationCanceledException())));Assert.Equal("persistence_unavailable",(await store.MoveAsync(new(s.Actor,s.Verifier,target.PublicId,1,null),default)).Error);Assert.Equal(before,await Snapshot(s));}

    private static IEnumerable<JsonElement> Nodes(JsonElement element){if(element.ValueKind==JsonValueKind.Object){yield return element;foreach(var p in element.EnumerateObject())foreach(var n in Nodes(p.Value))yield return n;}else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())foreach(var n in Nodes(child))yield return n;}
    private static async Task Past(DateTimeOffset t){var delay=t-DateTimeOffset.UtcNow;if(delay>TimeSpan.Zero)await Task.Delay(delay+TimeSpan.FromMilliseconds(100));}
    private sealed class Signal(TaskCompletionSource reached,string table):DbCommandInterceptor
    {private void Check(DbCommand c){if(c.CommandText.Contains("cerberus."+table)&&c.CommandText.Contains("FOR "))reached.TrySetResult();}public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){Check(c);return ValueTask.FromResult(r);}public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){Check(c);return ValueTask.FromResult(r);}}
    private sealed class BeforeTarget(Func<Task> action):DbCommandInterceptor
    {private bool used;public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){if(!used&&c.CommandText.Contains("cerberus.record WHERE")&&c.CommandText.Contains("FOR NO KEY UPDATE")){used=true;await action();}return r;}}
    private sealed class BeforeParent(Func<Task> action):DbCommandInterceptor
    {private bool used;public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){if(!used&&c.CommandText.StartsWith("UPDATE cerberus.folder SET")){used=true;await action();}return r;}}
    private sealed class Capture(PostgresFixture fixture,bool analyze=false):DbCommandInterceptor
    {
        public List<string> Locks{get;}=[];public string? Plan{get;private set;}
        private void Note(DbCommand c){if(c.CommandText.Contains("FOR UPDATE")||c.CommandText.Contains("FOR NO KEY UPDATE")||c.CommandText.Contains("FOR SHARE"))Locks.Add(c.CommandText);}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){Note(c);return ValueTask.FromResult(r);}
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){Note(c);if(analyze&&Plan is null&&c.CommandText.StartsWith("WITH RECURSIVE")){await using var db=fixture.CreateContext();await db.Database.OpenConnectionAsync(ct);await using var cmd=db.Database.GetDbConnection().CreateCommand();cmd.CommandText="EXPLAIN (ANALYZE, FORMAT JSON) "+c.CommandText;foreach(var p in c.Parameters.Cast<NpgsqlParameter>())cmd.Parameters.Add(p.Clone());Plan=(string)(await cmd.ExecuteScalarAsync(ct))!;}return r;}
    }

    private RecordMoveStore Store()=>new(fixture);
    private Task<VaultResult<RecordMoveDetails>> Move(ProfileSetup.State s,VaultRecord target,Guid? destination,long expected=1)=>Store().MoveAsync(new(s.Actor,s.Verifier,target.PublicId,expected,destination),default);
    private async Task Success(VaultRecord before,VaultFolder? destination,VaultFolder? from,VaultFolder? to,VaultResult<RecordMoveDetails> result)
    {
        Assert.Null(result.Error);var d=result.Data!;Assert.Equal(before.PublicId,d.RecordId);Assert.Equal(destination?.PublicId,d.FolderId);Assert.Equal(before.Revision+1,d.Revision);Assert.True(d.ServerSequence>before.ServerSequence);Assert.InRange(d.ServerSequence,1,ProtocolBinary.MaxInteger);
        var row=await Row(before.Id);Assert.Equal(destination?.Id,row.FolderId);Assert.Equal(before.Envelope,row.Envelope);Assert.Equal(before.EditedAt,row.EditedAt);Assert.Equal(before.AccountId,row.AccountId);Assert.Equal(d.Revision,row.Revision);Assert.Equal(d.ServerSequence,row.ServerSequence);Assert.NotEqual(before.ConcurrencyStamp,row.ConcurrencyStamp);Assert.Null(row.DeletedAt);Assert.Null(row.PurgeAt);
        foreach(var parent in new[]{from,to}.OfType<VaultFolder>().DistinctBy(x=>x.Id)){
            var current=await FolderRow(parent.Id);var changed=before.FolderId!=destination?.Id && (parent.Id==before.FolderId || parent.Id==destination?.Id);Assert.Equal(parent.Revision+(changed?1:0),current.Revision);if(changed){Assert.True(current.ServerSequence>parent.ServerSequence);Assert.NotEqual(parent.ConcurrencyStamp,current.ConcurrencyStamp);}else{Assert.Equal(parent.ServerSequence,current.ServerSequence);Assert.Equal(parent.ConcurrencyStamp,current.ConcurrencyStamp);}Assert.Equal(parent.Envelope,current.Envelope);Assert.Equal(parent.EditedAt,current.EditedAt);Assert.Equal(parent.ParentFolderId,current.ParentFolderId);
        }
        await using var db=fixture.CreateContext();Assert.False(await db.TrashEntries.AnyAsync(x=>x.ResourceKind=="record" && x.ResourceId==before.PublicId));
    }
    private async Task<VaultFolder> FolderRow(long id){await using var db=fixture.CreateContext();return await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==id);}
    private async Task<string> Stable(ProfileSetup.State s,long record,long[] folders){await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new{Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId&&x.Id!=record).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId&&!folders.Contains(x.Id)).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Links=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync(),Members=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),Protection=await db.VaultProtections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).ToArrayAsync(),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync()});}
    private static bool IsWrite(DbCommand c)=>c.CommandText.Contains("UPDATE cerberus.record AS");
    private sealed class BeforeWrite:DbCommandInterceptor
    {
        private readonly Func<DbCommand,Task> action;private bool used;
        public BeforeWrite(Func<Task> value):this(_=>value()){}public BeforeWrite(Func<DbCommand,Task> value)=>action=value;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> result,CancellationToken ct=default){if(!used&&IsWrite(c)){used=true;await action(c);}return result;}
    }
    private sealed class Fail(string stage):DbCommandInterceptor
    {
        private bool written,failed;private int parents,parentsDone;
        private void Check(DbCommand c){if(failed)return;if(c.CommandText.StartsWith("UPDATE cerberus.folder SET"))parents++;if((stage=="afterRecord"&&written)||(stage=="firstParent"&&parents==1)||(stage=="secondParent"&&parents==2)||(stage=="afterFirstParent"&&parentsDone>=1)||(stage=="afterSecondParent"&&parentsDone>=2)){failed=true;throw new TimeoutException("controlled move persistence failure");}}
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int result,CancellationToken ct=default){if(IsWrite(c))written=true;if(c.CommandText.StartsWith("UPDATE cerberus.folder SET"))parentsDone++;return ValueTask.FromResult(result);}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> result,CancellationToken ct=default){Check(c);return ValueTask.FromResult(result);}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> result,CancellationToken ct=default){Check(c);return ValueTask.FromResult(result);}
    }
    private async Task<string> Snapshot(ProfileSetup.State s){await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new{Accounts=await db.Accounts.AsNoTracking().Where(x=>x.Id==s.InternalId).ToArrayAsync(),Protection=await db.VaultProtections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).ToArrayAsync(),ProfileFolders=await db.ProfileFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileCollections=await db.ProfileCollections.AsNoTracking().Where(x=>db.Profiles.Any(p=>p.Id==x.ProfileId&&p.AccountId==s.InternalId)).OrderBy(x=>x.ProfileId).ThenBy(x=>x.CollectionId).ToArrayAsync(),CollectionFolders=await db.CollectionFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.FolderId).ToArrayAsync(),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>x.RecipientAccountId==s.InternalId||db.Collections.Any(c=>c.Id==x.CollectionId&&c.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Links=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync(),Members=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),Operations=await db.TrashOperations.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Entries=await db.TrashEntries.AsNoTracking().Where(x=>db.TrashOperations.Any(o=>o.Id==x.OperationId&&o.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync(),Queue=await db.RetentionWorkItems.AsNoTracking().Where(x=>db.TrashOperations.Any(o=>o.AccountId==s.InternalId&&x.OperationKey=="trash/"+o.PublicId)).OrderBy(x=>x.Id).ToArrayAsync(),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync()});}
    private async Task<VaultRecord> Row(long id){await using var db=fixture.CreateContext();return await db.Records.AsNoTracking().SingleAsync(x=>x.Id==id);}
    private async Task<VaultRecord> Record(ProfileSetup.State s,long? folder=null){var row=new VaultRecord{PublicId=Guid.NewGuid(),AccountId=s.InternalId,FolderId=folder,Envelope=Bytes(ProfileSetup.Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.CreateContext();db.Records.Add(row);await db.SaveChangesAsync();return row;}
    private async Task<VaultFolder> Folder(ProfileSetup.State s,long? parent=null){var row=new VaultFolder{PublicId=Guid.NewGuid(),AccountId=s.InternalId,ParentFolderId=parent,Envelope=Bytes(ProfileSetup.Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.CreateContext();db.Folders.Add(row);await db.SaveChangesAsync();return row;}
    private async Task<Profile> Profile(ProfileSetup.State s,ProtectionFixture owner,ProtectionFixture scoped,Guid[]? records=null,Guid[]? folders=null,Guid[]? collections=null){var input=ProfileSetup.Input(s,owner,scoped) with{RecordIds=records??[],FolderIds=folders??[],CollectionIds=collections??[]};Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);await using var db=fixture.CreateContext();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==input.ProfileId);}
    private async Task Members(ProfileSetup.State s,VaultCollection collection,long[] records,long[] folders){await using var db=fixture.CreateContext();db.CollectionRecords.AddRange(records.Select(x=>new CollectionRecord{AccountId=s.InternalId,CollectionId=collection.Id,RecordId=x}));db.CollectionFolders.AddRange(folders.Select(x=>new CollectionFolder{AccountId=s.InternalId,CollectionId=collection.Id,FolderId=x}));await db.SaveChangesAsync();}
    private async Task<string> Selected(ProfileSetup.State s,long profile){var verifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));await using var db=fixture.CreateContext();db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,ProfileId=profile,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return verifier;}
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private static byte[] Malformed(byte[] raw,string kind){var s=Encoding.UTF8.GetString(raw);return Encoding.UTF8.GetBytes(kind switch{"numericString"=>s.Replace("\"keyEpoch\":1","\"keyEpoch\":\"1\""),"unknown"=>s.Replace("{","{\"secret\":1,"),"duplicate"=>s.Replace("{","{\"keyEpoch\":1,"),_=>s.Replace("\"format\"","\"Format\"")});}
}
