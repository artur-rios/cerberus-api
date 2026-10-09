from pathlib import Path
w=Path('/tmp/cerberus-uc20-worktree')
old=(w/'tests/Infrastructure/ArturRios.Cerberus.Data.Tests/RecordUpdateStoreTests.cs').read_text()
helpers='\n'.join(l for l in old.splitlines() if any(l.startswith('    private '+p) for p in ['async Task<VaultRecord> Row(', 'async Task<VaultRecord> Record(', 'async Task<VaultFolder> Folder(', 'async Task<Profile> Profile(', 'async Task Members(', 'async Task<string> Selected(', 'static byte[] Bytes<', 'static byte[] Malformed(']))
source=r'''using System.Data.Common;
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
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class RecordTrashStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("wide")][InlineData("direct")][InlineData("folder")][InlineData("collection")][InlineData("descendant")]
    public async Task GivenCurrentOwnedScope_WhenTrashing_ThenOneRecoverableOperationAndPreservedCipher(string route)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);var child=await Folder(s,a.Folder.Id);var target=await Record(s,route is "folder" or "descendant"?child.Id:null);
        if(route is "collection" or "descendant")await Members(s,a.Collection,route=="collection"?[target.Id]:[],route=="descendant"?[a.Folder.Id]:[]);
        if(route!="wide"){var p=await Profile(s,owner,scoped,records:route=="direct"?[target.PublicId]:[],folders:route=="folder"?[a.Folder.PublicId]:[],collections:route is "collection" or "descendant"?[a.Collection.PublicId]:[]);s=s with{Verifier=await Selected(s,p.Id)};}
        var r=await Trash(s,target);await Success(s,target,r);
        await using var db=fixture.CreateContext();Assert.False((await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier)).Revoked);
        Assert.Equal("not_found",(await new RecordReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,target.PublicId),default)).Error);
        var list=await new RecordListStore(fixture).ListAsync(new(s.Actor,s.Verifier,50,0,null),default);Assert.Null(list.Error);Assert.DoesNotContain(list.Data!.Items,x=>x.RecordId==target.PublicId);
    }
    [FunctionalFact]
    public async Task GivenAllStoredAssociationsIncludingInactive_WhenTrashing_ThenRetainTypedSnapshotAndBumpOnlyActiveDirectParents()
    {
        using var owner=new ProtectionFixture();using var k1=new ProtectionFixture();using var k2=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);var b=await AssociationSetup.Items(fixture,s);var child=await Folder(s,a.Folder.Id);var target=await Record(s,child.Id);await Members(s,a.Collection,[target.Id],[a.Folder.Id]);await Members(s,b.Collection,[target.Id],[]);var p=await Profile(s,owner,k1,records:[target.PublicId],folders:[a.Folder.PublicId],collections:[a.Collection.PublicId]);var q=await Profile(s,owner,k2,records:[target.PublicId]);
        await using(var db=fixture.CreateContext()){await db.Profiles.Where(x=>x.Id==q.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));await db.Collections.Where(x=>x.Id==b.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));}
        var otherBefore=await Row(a.Record.Id);var r=await Trash(s,target);await Success(s,target,r);
        await using(var db=fixture.CreateContext()){
            var op=await db.TrashOperations.SingleAsync(x=>x.PublicId==r.Data!.TrashOperationId);var entry=await db.TrashEntries.SingleAsync(x=>x.OperationId==op.Id);var snap=JsonSerializer.Deserialize<RecordAssociationSnapshot>(entry.AssociationSnapshot,ProtectionFixture.Json)!;
            Assert.Equal(child.PublicId,snap.FolderId);Assert.Equal(new[]{p.PublicId,q.PublicId}.Order(),snap.ProfileIds);Assert.Equal(new[]{a.Collection.PublicId,b.Collection.PublicId}.Order(),snap.CollectionIds);
            Assert.False(await db.ProfileRecords.AnyAsync(x=>x.RecordId==target.Id));Assert.False(await db.CollectionRecords.AnyAsync(x=>x.RecordId==target.Id));
            var cp=await db.Profiles.AsNoTracking().SingleAsync(x=>x.Id==p.Id);Assert.Equal(p.Revision+1,cp.Revision);Assert.True(cp.ServerSequence>p.ServerSequence);Assert.Equal(p.EditedAt,cp.EditedAt);Assert.Equal(p.Envelope,cp.Envelope);Assert.Equal(p.KeyWrappers,cp.KeyWrappers);
            var cq=await db.Profiles.AsNoTracking().SingleAsync(x=>x.Id==q.Id);Assert.Equal(q.Revision,cq.Revision);Assert.Equal(q.ServerSequence,cq.ServerSequence);
            var cc=await db.Collections.AsNoTracking().SingleAsync(x=>x.Id==a.Collection.Id);Assert.Equal(a.Collection.Revision+1,cc.Revision);Assert.True(cc.ServerSequence>a.Collection.ServerSequence);Assert.Equal(a.Collection.Envelope,cc.Envelope);Assert.Equal(a.Collection.EditedAt,cc.EditedAt);Assert.Equal(a.Collection.KeyEpoch,cc.KeyEpoch);
            var cb=await db.Collections.AsNoTracking().SingleAsync(x=>x.Id==b.Collection.Id);Assert.Equal(b.Collection.Revision,cb.Revision);Assert.Equal(b.Collection.ServerSequence,cb.ServerSequence);
            var cf=await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==child.Id);Assert.Equal(child.Revision+1,cf.Revision);Assert.True(cf.ServerSequence>child.ServerSequence);Assert.Equal(child.Envelope,cf.Envelope);Assert.Equal(child.EditedAt,cf.EditedAt);Assert.Equal(a.Folder.Revision,(await db.Folders.SingleAsync(x=>x.Id==a.Folder.Id)).Revision);
            Assert.True(await db.ProfileFolders.AnyAsync(x=>x.ProfileId==p.Id && x.FolderId==a.Folder.Id));Assert.True(await db.ProfileCollections.AnyAsync(x=>x.ProfileId==p.Id && x.CollectionId==a.Collection.Id));Assert.True(await db.CollectionFolders.AnyAsync(x=>x.CollectionId==a.Collection.Id && x.FolderId==a.Folder.Id));
        }
        Assert.Equal(Bytes(otherBefore),Bytes(await Row(a.Record.Id)));
    }
    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly,false,false)][InlineData(CollectionGrantAccess.ReadWrite,false,false)][InlineData(CollectionGrantAccess.ReadOnly,true,true)][InlineData(CollectionGrantAccess.ReadWrite,true,true)]
    public async Task GivenNativeVisibleForeignRecord_WhenTrashingWithStaleRevisionAndCorruptCipher_Then403NoMutation(CollectionGrantAccess access,bool selected,bool inherited)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var target=await Record(o,inherited?a.Folder.Id:null);await Members(o,a.Collection,inherited?[]:[target.Id],inherited?[a.Folder.Id]:[]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,access);
        if(selected){var p=await Profile(s,client,scoped,collections:[a.Collection.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};}
        await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,"damaged"u8.ToArray()));var before=await Snapshot(o);Assert.Equal("vault_access_denied",(await Trash(s,target,99)).Error);Assert.Equal(before,await Snapshot(o));
    }
    [FunctionalTheory][InlineData("missing")][InlineData("foreign")][InlineData("unselected")][InlineData("trash")][InlineData("recordTerminal")][InlineData("ancestorTrash")][InlineData("ancestorTerminal")][InlineData("ownerClosing")][InlineData("ownerTerminal")][InlineData("grantRevoked")][InlineData("collectionTrash")][InlineData("removedMembership")]
    public async Task GivenHiddenTargetWithBadNativeAndStaleRevision_WhenTrashing_Then404BeforeDisclosure(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=kind=="unselected"?o:await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var target=await Record(o,a.Folder.Id);await Members(o,a.Collection,[target.Id],[]);CollectionGrant? g=null;if(kind!="foreign" && s.InternalId!=o.InternalId)g=await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);if(kind=="unselected"){var p=await Profile(s,owner,scoped);s=s with{Verifier=await Selected(s,p.Id)};}
        await using(var db=fixture.CreateContext()){
            await db.Records.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,"bad"u8.ToArray()));
            if(g is not null)await db.CollectionGrants.Where(x=>x.Id==g.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.RecipientKeyEnvelope,"bad"u8.ToArray()));
            if(kind=="trash")await db.Records.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));if(kind=="ancestorTrash")await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));
            if(kind.EndsWith("Terminal")){var id=kind=="recordTerminal"?target.PublicId:kind=="ancestorTerminal"?a.Folder.PublicId:o.AccountId;db.TerminalErasures.Add(new(){ResourceId=id,ResourceKind=kind=="recordTerminal"?"record":kind=="ancestorTerminal"?"folder":"account",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
            if(kind=="ownerClosing")await db.Accounts.Where(x=>x.Id==o.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.State,AccountState.ClosurePending));if(kind=="grantRevoked")await db.CollectionGrants.Where(x=>x.Id==g!.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.State,CollectionGrantState.Revoked));if(kind=="collectionTrash")await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));if(kind=="removedMembership")await db.CollectionRecords.Where(x=>x.RecordId==target.Id).ExecuteDeleteAsync();
        }
        var before=await Snapshot(o);Assert.Equal("not_found",(await Store().TrashAsync(new(s.Actor,s.Verifier,kind=="missing"?Guid.NewGuid():target.PublicId,99),default)).Error);Assert.Equal(before,await Snapshot(o));
    }
    [FunctionalTheory][InlineData("missing","not_found")][InlineData("closing","not_found")][InlineData("terminal","not_found")][InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")][InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")][InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")][InlineData("nullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)][InlineData("selection","vault_access_denied")]
    public async Task GivenInvalidCurrentAuthority_WhenTrashing_ThenFailClosed(string kind,string? error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);
        await using(var db=fixture.CreateContext()){var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);if(kind=="closing")a.State=AccountState.ClosurePending;if(kind=="terminal")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="zeroPolicy")v.PolicyRevision=0;if(kind=="zeroGeneration")v.RevocationGeneration=0;if(kind is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(kind is "nullExpiry" or "noExpiry")v.ExpiresAt=null;if(kind=="selection")v.ProfileId=long.MaxValue;await db.SaveChangesAsync();}
        var before=await Snapshot(s);var r=await Store().TrashAsync(new(kind=="missing"?Guid.NewGuid():s.Actor,kind=="access"?new string('a',64):s.Verifier,target.PublicId,1),default);Assert.Equal(error,r.Error);if(error is null)await Success(s,target,r);else Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("actor","authentication_required")][InlineData("id","validation_failed")][InlineData("zero","validation_failed")][InlineData("unsafe","validation_failed")]
    public async Task GivenInvalidInternalRequest_WhenTrashing_ThenRejectWithoutWrite(string kind,string error)
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);var before=await Snapshot(s);var r=await Store().TrashAsync(new(kind=="actor"?Guid.Empty:s.Actor,s.Verifier,kind=="id"?Guid.Empty:target.PublicId,kind=="zero"?0:kind=="unsafe"?ProtocolBinary.MaxInteger+1:1),default);Assert.Equal(error,r.Error);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("zeroRevision","persistence_unavailable")][InlineData("zeroSequence","persistence_unavailable")][InlineData("unsafeSequence","persistence_unavailable")][InlineData("maxRevision","revision_conflict")][InlineData("stale","revision_conflict")][InlineData("purgeWithoutTrash","persistence_unavailable")][InlineData("oldEntry","revision_conflict")]
    public async Task GivenCorruptOrConflictingTargetState_WhenTrashing_ThenNoPartialOperation(string kind,string error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);await using(var db=fixture.CreateContext()){
            var t=await db.Records.SingleAsync(x=>x.Id==target.Id);if(kind=="zeroRevision")t.Revision=0;if(kind=="zeroSequence")t.ServerSequence=0;if(kind=="unsafeSequence")t.ServerSequence=ProtocolBinary.MaxInteger+1;if(kind=="maxRevision")t.Revision=ProtocolBinary.MaxInteger;if(kind=="purgeWithoutTrash")t.PurgeAt=DateTimeOffset.UtcNow.AddDays(30);
            if(kind=="oldEntry"){var op=new TrashOperation{AccountId=s.InternalId,RootResourceKind="record",RootResourceId=target.PublicId,DeletedAt=DateTimeOffset.UtcNow,PurgeAt=DateTimeOffset.UtcNow.AddDays(30)};db.TrashOperations.Add(op);await db.SaveChangesAsync();db.TrashEntries.Add(new(){OperationId=op.Id,ResourceKind="record",ResourceId=target.PublicId,AssociationSnapshot=Bytes(new RecordAssociationSnapshot(null,[],[]))});}await db.SaveChangesAsync();}
        var before=await Snapshot(s);Assert.Equal(error,(await Trash(s,target,kind=="maxRevision"?ProtocolBinary.MaxInteger:kind=="stale"?2:1)).Error);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("profile",false)][InlineData("collection",false)][InlineData("folder",false)][InlineData("profile",true)][InlineData("collection",true)][InlineData("folder",true)]
    public async Task GivenCorruptOrExhaustedActiveParent_WhenTrashing_ThenRollbackRecordAndEveryLink(string kind,bool exhausted)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);var target=await Record(s,a.Folder.Id);await Members(s,a.Collection,[target.Id],[]);var p=await Profile(s,owner,scoped,records:[target.PublicId]);await using(var db=fixture.CreateContext()){
            if(kind=="profile")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revision,exhausted?ProtocolBinary.MaxInteger:0));if(kind=="collection")await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revision,exhausted?ProtocolBinary.MaxInteger:0));if(kind=="folder")await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revision,exhausted?ProtocolBinary.MaxInteger:0));}
        var before=await Snapshot(s);Assert.Equal(exhausted?"revision_conflict":"persistence_unavailable",(await Trash(s,target)).Error);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenDamagedOwnedCipher_WhenTrashing_ThenRetainBytesWithoutDecryption()
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,"opaque-damaged"u8.ToArray()));target=await Row(target.Id);await Success(s,target,await Trash(s,target));}
    [FunctionalTheory][InlineData("signature")][InlineData("revision")][InlineData("epoch")][InlineData("numericString")][InlineData("unknown")][InlineData("duplicate")][InlineData("ownerPins")][InlineData("recipientPins")]
    public async Task GivenMalformedContributingNativeAuthority_WhenNonownerRequestsTrash_Then503WithoutMutation(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);await Members(o,a.Collection,[a.Record.Id],[]);var g=await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);await using(var db=fixture.CreateContext()){
            if(kind=="signature")await db.CollectionGrants.Where(x=>x.Id==g.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.RecipientKeyEnvelope,"bad"u8.ToArray()));if(kind=="revision")await db.CollectionGrants.Where(x=>x.Id==g.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revision,2));if(kind=="epoch")await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.KeyEpoch,2));if(kind is "numericString" or "unknown" or "duplicate")await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,Malformed(a.Collection.Envelope,kind)));if(kind is "ownerPins" or "recipientPins")await db.VaultProtections.Where(x=>x.AccountId==(kind=="ownerPins"?o.InternalId:s.InternalId)).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Material,"bad"u8.ToArray()));}
        var before=await Snapshot(o);Assert.Equal("persistence_unavailable",(await Trash(s,a.Record,99)).Error);Assert.Equal(before,await Snapshot(o));
    }
    [FunctionalFact]
    public async Task GivenConcurrentTrashAndRetry_WhenDeleting_ThenOneOperationAndNoExtendedDeadline()
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);var r=await Task.WhenAll(Trash(s,target),Trash(s,target));var win=Assert.Single(r,x=>x.Error is null);Assert.Equal("not_found",Assert.Single(r,x=>x.Error is not null).Error);await Success(s,target,win);var before=await Snapshot(s);Assert.Equal("not_found",(await Trash(s,target)).Error);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenActualEditAndTrashRace_WhenCommitting_ThenOnlyOneExpectedRevisionWins(bool recipient)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=recipient?await ProfileSetup.Create(fixture,client):o;var a=await AssociationSetup.Items(fixture,o);await Members(o,a.Collection,[a.Record.Id],[]);if(recipient)await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);
        var edit=new RecordUpdateStore(fixture).UpdateAsync(new(s.Actor,s.Verifier,a.Record.PublicId,new(1,ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch)),default);var trash=Trash(o,a.Record);await Task.WhenAll(edit,trash);Assert.True((edit.Result.Error is null)^(trash.Result.Error is null));if(edit.Result.Error is null){Assert.Equal("revision_conflict",trash.Result.Error);await NoOperation(a.Record.PublicId);}else{Assert.Equal("not_found",edit.Result.Error);await Success(o,a.Record,trash.Result);}Assert.Equal(2,(await Row(a.Record.Id)).Revision);
    }
    [FunctionalTheory][InlineData("account")][InlineData("session")][InlineData("profile")][InlineData("collection")][InlineData("folder")][InlineData("record")]
    public async Task GivenExpiryDuringAnyLockWaitWithStaleRevision_WhenTrashing_Then403BeforeConflict(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);var target=await Record(s,a.Folder.Id);await Members(s,a.Collection,[target.Id],[]);var p=await Profile(s,owner,scoped,records:[target.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));var before=await Snapshot(s);
        await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();var id=kind switch{"account"=>s.InternalId,"profile"=>p.Id,"collection"=>a.Collection.Id,"folder"=>a.Folder.Id,_=>target.Id};var sql=kind switch{"account"=>"SELECT FROM cerberus.account WHERE id={0} FOR UPDATE","session"=>"SELECT FROM cerberus.vault_access_session WHERE handle_verifier={0} FOR UPDATE","profile"=>"SELECT FROM cerberus.profile WHERE id={0} FOR UPDATE","collection"=>"SELECT FROM cerberus.collection WHERE id={0} FOR UPDATE","folder"=>"SELECT FROM cerberus.folder WHERE id={0} FOR UPDATE",_=>"SELECT FROM cerberus.record WHERE id={0} FOR UPDATE"};await held.Database.ExecuteSqlRawAsync(sql,kind=="session"?(object)s.Verifier:id);
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));var pending=new RecordTrashStore(new ProfileSetup.Factory(fixture,new Signal(reached,kind=="session"?"vault_access_session":kind))).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,99),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);await Past(deadline);await tx.CommitAsync();Assert.Equal("vault_access_denied",(await pending).Error);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("expiry","vault_access_denied")][InlineData("ancestorTrash","not_found")][InlineData("reparentHidden","not_found")][InlineData("recordTerminal","not_found")][InlineData("ownerTerminal","not_found")]
    public async Task GivenChangeImmediatelyBeforeGuardedWrite_WhenTrashing_ThenCurrentStateDenies(string kind,string error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var root=await Folder(s);var child=await Folder(s,root.Id);var target=await Record(s,child.Id);var hidden=await Folder(s);await using(var db=fixture.CreateContext())await db.Folders.Where(x=>x.Id==hidden.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));var deadline=DateTimeOffset.UtcNow.AddSeconds(2);if(kind=="expiry")await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));
        var interceptor=new BeforeWrite(async()=>{if(kind=="expiry"){await Past(deadline);return;}await using var db=fixture.CreateContext();if(kind=="ancestorTrash")await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));else if(kind=="reparentHidden")await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ParentFolderId,(long?)hidden.Id));else{db.TerminalErasures.Add(new(){ResourceId=kind=="recordTerminal"?target.PublicId:s.AccountId,ResourceKind=kind=="recordTerminal"?"record":"account",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}});
        Assert.Equal(error,(await new RecordTrashStore(new ProfileSetup.Factory(fixture,interceptor)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,1),default)).Error);Assert.Equal(Bytes(target),Bytes(await Row(target.Id)));await NoOperation(target.PublicId);
    }
    [FunctionalTheory][InlineData("profile")][InlineData("collection")]
    public async Task GivenNewReferencingAssociationBeforeTargetLock_WhenTrashing_Then409WithoutUsingUnheldParent(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);var a=await AssociationSetup.Items(fixture,s);var p=await Profile(s,owner,scoped);var hook=new BeforeTarget(async()=>{await using var db=fixture.CreateContext();if(kind=="profile")db.ProfileRecords.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,RecordId=target.Id});else db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,RecordId=target.Id});await db.SaveChangesAsync();});
        Assert.Equal("revision_conflict",(await new RecordTrashStore(new ProfileSetup.Factory(fixture,hook)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,1),default)).Error);Assert.Equal(Bytes(target),Bytes(await Row(target.Id)));await NoOperation(target.PublicId);await using var check=fixture.CreateContext();Assert.True(kind=="profile"?await check.ProfileRecords.AnyAsync(x=>x.RecordId==target.Id):await check.CollectionRecords.AnyAsync(x=>x.RecordId==target.Id));
    }
    [FunctionalTheory][InlineData("write")][InlineData("links")][InlineData("parent")][InlineData("operation")][InlineData("entry")][InlineData("queue")]
    public async Task GivenFailureAfterPartialTrashWork_WhenDeleting_ThenWholeGraphRollsBackAndRetrySucceeds(string stage)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);var target=await Record(s,a.Folder.Id);await Members(s,a.Collection,[target.Id],[]);await Profile(s,owner,scoped,records:[target.PublicId]);var before=await Snapshot(s);
        Assert.Equal("persistence_unavailable",(await new RecordTrashStore(new ProfileSetup.Factory(fixture,new Fail(stage))).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,1),default)).Error);Assert.Equal(before,await Snapshot(s));await Success(s,target,await Trash(s,target));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenSequenceExhaustionOrRegression_WhenTrashing_ThenNoCommittedPartialGraph(bool regression)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);var before=await Snapshot(s);await using var db=fixture.CreateContext();var previous=await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();try{if(regression)await db.Database.ExecuteSqlRawAsync("SELECT setval('cerberus.server_sequence',1,false)");else await db.Database.ExecuteSqlRawAsync("SELECT setval('cerberus.server_sequence',9007199254740991,true)");Assert.Equal(regression?"persistence_unavailable":"revision_conflict",(await Trash(s,target)).Error);Assert.Equal(before,await Snapshot(s));}finally{await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{previous},true)");}
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenActualRotationAfterTrash_WhenRotating_ThenRetainedCipherRemainsRequiredAndTrashStatePreserved(bool omit)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);var tr=await Trash(s,target);await Success(s,target,tr);var before=await Snapshot(s);
        var all=new[]{new ContentReplacement("account",s.AccountId,1,ProfileSetup.Envelope(2)),new ContentReplacement("record",target.PublicId,2,ProfileSetup.Envelope(2))};var change=new ProtectionChange(s.AccountId,1,1,"rotate-content",owner.Rewrap(),omit?all[..1]:all);var raw=Bytes(change);var challenge=(await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;var r=await new VaultProtectionChangeStore(fixture).ChangeAsync(new(s.Actor,s.Verifier,challenge.ChallengeId,owner.Sign(challenge),raw,change),default);
        if(omit){Assert.Equal("validation_failed",r.Error);var row=await Row(target.Id);Assert.Equal(tr.Data!.DeletedAt,row.DeletedAt);Assert.Equal(2,row.Revision);}else{Assert.Null(r.Error);var row=await Row(target.Id);Assert.Equal(tr.Data!.DeletedAt,row.DeletedAt);Assert.Equal(tr.Data.PurgeAt,row.PurgeAt);Assert.Equal(3,row.Revision);Assert.Equal(2,JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,ProtectionFixture.Json)!.KeyEpoch);await using var db=fixture.CreateContext();Assert.Single(await db.TrashEntries.Where(x=>x.ResourceId==target.PublicId).ToListAsync());Assert.Single(await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+tr.Data.TrashOperationId).ToListAsync());}
    }
    [FunctionalFact]
    public async Task GivenOutageOrCallerCancellation_WhenTrashing_Then503OrPropagatedCancellation()
    {var r=new RecordTrashRequest(Guid.NewGuid(),new string('a',64),Guid.NewGuid(),1);var dead=new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=absent;Username=fixture;Password=fixture;Timeout=1").Options);Assert.Equal("persistence_unavailable",(await new RecordTrashStore(dead).TrashAsync(r,default)).Error);using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().TrashAsync(r,ct.Token));}

    private RecordTrashStore Store()=>new(fixture);
    private Task<VaultResult<RecordTrashDetails>> Trash(ProfileSetup.State s,VaultRecord r,long revision=1)=>Store().TrashAsync(new(s.Actor,s.Verifier,r.PublicId,revision),default);
    private async Task Success(ProfileSetup.State s,VaultRecord before,VaultResult<RecordTrashDetails> result)
    {
        Assert.Null(result.Error);var d=result.Data!;Assert.Equal(before.PublicId,d.RecordId);Assert.NotEqual(Guid.Empty,d.TrashOperationId);Assert.Equal(before.Revision+1,d.Revision);Assert.True(d.ServerSequence>before.ServerSequence);Assert.InRange(d.ServerSequence,1,ProtocolBinary.MaxInteger);Assert.Equal(TimeSpan.Zero,d.DeletedAt.Offset);Assert.Equal(0,d.DeletedAt.Ticks%10);Assert.Equal(TimeSpan.FromDays(30),d.PurgeAt-d.DeletedAt);Assert.Equal(TimeSpan.Zero,d.PurgeAt.Offset);
        var row=await Row(before.Id);Assert.Equal(before.Envelope,row.Envelope);Assert.Equal(before.EditedAt,row.EditedAt);Assert.Equal(before.AccountId,row.AccountId);Assert.Null(row.FolderId);Assert.Equal(d.Revision,row.Revision);Assert.Equal(d.ServerSequence,row.ServerSequence);Assert.Equal(d.DeletedAt,row.DeletedAt);Assert.Equal(d.PurgeAt,row.PurgeAt);Assert.NotEqual(before.ConcurrencyStamp,row.ConcurrencyStamp);
        await using var db=fixture.CreateContext();var op=await db.TrashOperations.SingleAsync(x=>x.PublicId==d.TrashOperationId);Assert.Equal(s.InternalId,op.AccountId);Assert.Equal("record",op.RootResourceKind);Assert.Equal(before.PublicId,op.RootResourceId);Assert.Equal(d.DeletedAt,op.DeletedAt);Assert.Equal(d.PurgeAt,op.PurgeAt);var entry=await db.TrashEntries.SingleAsync(x=>x.OperationId==op.Id);Assert.Equal("record",entry.ResourceKind);Assert.Equal(before.PublicId,entry.ResourceId);var q=await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="trash/"+d.TrashOperationId);Assert.Equal(d.PurgeAt,q.DueAt);Assert.Null(q.CompletedAt);Assert.False(await db.ProfileRecords.AnyAsync(x=>x.RecordId==before.Id));Assert.False(await db.CollectionRecords.AnyAsync(x=>x.RecordId==before.Id));
    }
    private async Task NoOperation(Guid target){await using var db=fixture.CreateContext();Assert.False(await db.TrashEntries.AnyAsync(x=>x.ResourceKind=="record" && x.ResourceId==target));Assert.False(await db.TrashOperations.AnyAsync(x=>x.RootResourceKind=="record" && x.RootResourceId==target));}
    private async Task<string> Snapshot(ProfileSetup.State s){await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new{Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Links=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync(),Members=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),Operations=await db.TrashOperations.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Entries=await db.TrashEntries.AsNoTracking().Where(x=>db.TrashOperations.Any(o=>o.Id==x.OperationId&&o.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync(),Queue=await db.RetentionWorkItems.AsNoTracking().Where(x=>db.TrashOperations.Any(o=>o.AccountId==s.InternalId&&x.OperationKey=="trash/"+o.PublicId)).OrderBy(x=>x.Id).ToArrayAsync(),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync()});}
    private static async Task Past(DateTimeOffset t){var delay=t-DateTimeOffset.UtcNow;if(delay>TimeSpan.Zero)await Task.Delay(delay+TimeSpan.FromMilliseconds(100));}
    private static bool IsWrite(DbCommand c)=>c.CommandText.Contains("UPDATE cerberus.record AS");
    private sealed class Signal(TaskCompletionSource reached,string table):DbCommandInterceptor
    {private void Check(DbCommand c){if(c.CommandText.Contains("cerberus."+table)&&c.CommandText.Contains("FOR "))reached.TrySetResult();}public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){Check(c);return ValueTask.FromResult(r);}public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){Check(c);return ValueTask.FromResult(r);}}
    private sealed class BeforeWrite(Func<Task> action):DbCommandInterceptor
    {private bool done;public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){if(!done&&IsWrite(c)){done=true;await action();}return r;}}
    private sealed class BeforeTarget(Func<Task> action):DbCommandInterceptor
    {private bool done;public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){if(!done&&c.CommandText.Contains("cerberus.record")&&c.CommandText.Contains("FOR UPDATE")){done=true;await action();}return r;}}
    private sealed class Fail(string stage):DbCommandInterceptor
    {
        private bool written,failed;
        private void Check(DbCommand c){if(failed)return;var s=c.CommandText;var hit=stage=="write"&&written || stage=="links"&&s.Contains("DELETE FROM cerberus.profile_record") || stage=="parent"&&(s.Contains("UPDATE cerberus.profile SET")||s.Contains("UPDATE cerberus.collection SET")||s.Contains("UPDATE cerberus.folder SET")) || stage=="operation"&&s.Contains("INSERT INTO cerberus.trash_operation") || stage=="entry"&&s.Contains("INSERT INTO cerberus.trash_entry") || stage=="queue"&&s.Contains("INSERT INTO cerberus.retention_work_item");if(hit){failed=true;throw new TimeoutException("injected recordtrash persistence fault");}}
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default){if(IsWrite(c))written=true;return ValueTask.FromResult(r);}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){Check(c);return ValueTask.FromResult(r);}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){Check(c);return ValueTask.FromResult(r);}
    }
'''
(w/'tests/Infrastructure/ArturRios.Cerberus.Data.Tests/RecordTrashStoreTests.cs').write_text(source+'\n'+helpers+'\n}\n')
print('Wrote first RecordTrashStore behavioral matrix before product.')
