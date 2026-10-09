using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Cerberus.Data.Protection;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class FolderUpdateStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("wide")][InlineData("direct")][InlineData("folder")][InlineData("collectionTarget")][InlineData("collectionFolder")]
    public async Task GivenCurrentOwnedScope_WhenUpdating_ThenOnlyTargetContentAndMetadataAdvance(string route)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);var child=await Folder(s,a.Folder.Id);var target=await Folder(s,child.Id);
        await Members(s,a.Collection,[],route=="collectionTarget"?[target.Id]:route=="collectionFolder"?[a.Folder.Id]:[]);
        var p=await Profile(s,owner,key,folders:route=="direct"?[target.PublicId]:route=="folder"?[a.Folder.PublicId]:[],collections:route.StartsWith("collection")?[a.Collection.PublicId]:[]);
        var actor=route=="wide"?s:s with{Verifier=await Selected(s,p.Id)};var before=await Unrelated(s,target.Id);var input=Input();var result=await Update(actor,target,input);
        await Success(target,input,result);Assert.Equal(before,await Unrelated(s,target.Id));
    }
    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly,false,"direct")][InlineData(CollectionGrantAccess.ReadOnly,true,"folder")]
    [InlineData(CollectionGrantAccess.ReadWrite,false,"direct")][InlineData(CollectionGrantAccess.ReadWrite,true,"direct")][InlineData(CollectionGrantAccess.ReadWrite,false,"folder")][InlineData(CollectionGrantAccess.ReadWrite,true,"folder")]
    public async Task GivenNativeSharedScope_WhenUpdating_ThenOnlyReadWriteMayChangeOriginalOwnersContent(CollectionGrantAccess access,bool selected,string route)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var child=await Folder(o,a.Folder.Id);var target=await Folder(o,child.Id);
        await Members(o,a.Collection,[],route=="direct"?[target.Id]:[a.Folder.Id]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,access);var p=await Profile(s,client,key,collections:[a.Collection.PublicId]);var actor=selected?s with{Verifier=await Selected(s,p.Id)}:s;
        var before=await Snapshot(o);var unchanged=await Unrelated(o,target.Id);var recipient=await Snapshot(s);var input=Input();var r=await Update(actor,target,input);
        if(access==CollectionGrantAccess.ReadOnly){Assert.Equal("vault_access_denied",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(o));}else{await Success(target,input,r);Assert.Equal(unchanged,await Unrelated(o,target.Id));}
        Assert.Equal(recipient,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("missing")][InlineData("foreign")][InlineData("unselected")][InlineData("folderTrash")][InlineData("ancestorTrash")][InlineData("folderTerminal")][InlineData("ancestorTerminal")][InlineData("ownerClosing")][InlineData("collectionTrash")][InlineData("grantRevoked")]
    public async Task GivenHiddenTargetWithStaleRevisionAndBadCipher_WhenUpdating_Then404BeforeCorruptionOrConflict(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=kind=="unselected"?o:await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var target=await Folder(o,a.Folder.Id);await Members(o,a.Collection,[],[target.Id]);
        CollectionGrant? grant=null;if(kind!="foreign"&&s.InternalId!=o.InternalId)grant=await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);
        if(kind=="unselected"){var p=await Profile(s,owner,key);s=s with{Verifier=await Selected(s,p.Id)};}
        await using(var db=fixture.CreateContext()){
            await db.Folders.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()).SetProperty(r=>r.DeletedAt,kind=="folderTrash"?(DateTimeOffset?)DateTimeOffset.UtcNow:null));
            if(kind=="ancestorTrash")await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.DeletedAt,DateTimeOffset.UtcNow));
            if(kind.EndsWith("Terminal")){db.TerminalErasures.Add(new(){ResourceId=kind=="folderTerminal"?target.PublicId:a.Folder.PublicId,ResourceKind=kind=="folderTerminal"?"folder":"folder",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
            if(kind=="ownerClosing")await db.Accounts.Where(x=>x.Id==o.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));
            if(kind=="collectionTrash")await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.DeletedAt,DateTimeOffset.UtcNow));
            if(kind=="grantRevoked")await db.CollectionGrants.Where(x=>x.Id==grant!.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Revoked));
        }
        var before=await Snapshot(o);var r=await Store().UpdateAsync(new(s.Actor,s.Verifier,kind=="missing"?Guid.NewGuid():target.PublicId,Input(77)),default);Assert.Equal("not_found",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(o));
    }
    [FunctionalTheory][InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")][InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")][InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")][InlineData("nullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)][InlineData("selection","vault_access_denied")][InlineData("closing","not_found")][InlineData("actorAbsent","not_found")]
    public async Task GivenInvalidCurrentAccountOrSession_WhenUpdating_ThenNoMutation(string kind,string? error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Folder(s);
        await using(var db=fixture.CreateContext()){var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);if(kind=="closing")a.State=AccountState.ClosurePending;if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="zeroPolicy")v.PolicyRevision=0;if(kind=="zeroGeneration")v.RevocationGeneration=0;if(kind is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(kind is "nullExpiry" or "noExpiry")v.ExpiresAt=null;if(kind=="selection")v.ProfileId=long.MaxValue;await db.SaveChangesAsync();}
        var before=await Snapshot(s);var r=await Store().UpdateAsync(new(kind=="actorAbsent"?Guid.NewGuid():s.Actor,kind=="access"?new string('a',64):s.Verifier,target.PublicId,Input()),default);Assert.Equal(error,r.Error);if(error is not null){Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));}
    }
    [FunctionalTheory][InlineData("actor","authentication_required")][InlineData("id","validation_failed")][InlineData("null","validation_failed")][InlineData("revision","validation_failed")][InlineData("unsafeRevision","validation_failed")][InlineData("time","validation_failed")][InlineData("offset","validation_failed")][InlineData("envelope","validation_failed")]
    public async Task GivenInvalidInternalInput_WhenUpdating_ThenRejectBeforeWrites(string kind,string error)
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Folder(s);var i=Input();i=kind switch{"revision"=>i with{ExpectedRevision=0},"unsafeRevision"=>i with{ExpectedRevision=ProtocolBinary.MaxInteger+1},"time"=>i with{EditedAt=DateTimeOffset.MinValue.AddTicks(9)},"offset"=>i with{EditedAt=i.EditedAt.ToOffset(TimeSpan.FromHours(1))},"envelope"=>i with{Envelope=null!},_=>i};var before=await Snapshot(s);var r=await Store().UpdateAsync(new(kind=="actor"?Guid.Empty:s.Actor,s.Verifier,kind=="id"?Guid.Empty:target.PublicId,kind=="null"?null!:i),default);Assert.Equal(error,r.Error);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("stale","revision_conflict")][InlineData("max","revision_conflict")][InlineData("revision","persistence_unavailable")][InlineData("sequence","persistence_unavailable")][InlineData("unsafeSequence","persistence_unavailable")][InlineData("envelope","persistence_unavailable")][InlineData("numericString","persistence_unavailable")][InlineData("unknown","persistence_unavailable")][InlineData("duplicate","persistence_unavailable")][InlineData("case","persistence_unavailable")][InlineData("epoch","validation_failed")][InlineData("salt","validation_failed")][InlineData("nonce","validation_failed")]
    public async Task GivenInvalidTargetOrReplacementContext_WhenUpdating_ThenPreserveEntireState(string kind,string error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Folder(s);var old=JsonSerializer.Deserialize<EncryptedEnvelope>(target.Envelope,ProtectionFixture.Json)!;var i=Input(kind=="stale"?8:kind=="max"?ProtocolBinary.MaxInteger:1);
        if(kind=="epoch")i=i with{Envelope=i.Envelope with{KeyEpoch=2}};if(kind=="salt")i=i with{Envelope=i.Envelope with{KeySalt=old.KeySalt}};if(kind=="nonce")i=i with{Envelope=i.Envelope with{Nonce=old.Nonce}};
        await using(var db=fixture.CreateContext()){var r=await db.Folders.SingleAsync(x=>x.Id==target.Id);if(kind=="max")r.Revision=ProtocolBinary.MaxInteger;if(kind=="revision")r.Revision=0;if(kind=="sequence")r.ServerSequence=0;if(kind=="unsafeSequence")r.ServerSequence=ProtocolBinary.MaxInteger+1;if(kind=="envelope")r.Envelope="bad"u8.ToArray();if(kind is "numericString" or "unknown" or "duplicate" or "case")r.Envelope=Malformed(r.Envelope,kind);await db.SaveChangesAsync();}
        var before=await Snapshot(s);var result=await Update(s,target,i);Assert.Equal(error,result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("minimum")][InlineData("pre2000")][InlineData("older")][InlineData("equal")][InlineData("sameEnvelope")]
    public async Task GivenMatchingRevisionAndRepresentableEdit_WhenUpdating_ThenUseRevisionInsteadOfTimestampWins(string kind)
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Folder(s);var i=Input() with{EditedAt=kind=="minimum"?new(10,TimeSpan.Zero):kind=="pre2000"?new DateTimeOffset(1999,12,31,23,59,59,TimeSpan.Zero).AddTicks(19):kind=="equal"?target.EditedAt:target.EditedAt.AddTicks(-11)};if(kind=="sameEnvelope")i=i with{Envelope=JsonSerializer.Deserialize<EncryptedEnvelope>(target.Envelope,ProtectionFixture.Json)!};await Success(target,i,await Update(s,target,i));}
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenConcurrentSameRevisionAndLostResponseRetry_WhenUpdating_ThenExactlyOneWinner(bool recipients)
    {
        using var owner=new ProtectionFixture();using var c1=new ProtectionFixture();using var c2=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,o);var target=a.Folder;await Members(o,a.Collection,[],[target.Id]);var s1=recipients?await ProfileSetup.Create(fixture,c1):o;var s2=recipients?await ProfileSetup.Create(fixture,c2):o;if(recipients){await AssociationSetup.Grant(fixture,o,owner,s1,c1,a.Collection,CollectionGrantAccess.ReadWrite);await AssociationSetup.Grant(fixture,o,owner,s2,c2,a.Collection,CollectionGrantAccess.ReadWrite);}
        var i1=Input();var i2=Input();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));var tasks=new[]{Store().UpdateAsync(new(s1.Actor,s1.Verifier,target.PublicId,i1),ct.Token),Store().UpdateAsync(new(s2.Actor,s2.Verifier,target.PublicId,i2),ct.Token)};var r=await Task.WhenAll(tasks);Assert.Single(r,x=>x.Error is null);Assert.Equal("revision_conflict",Assert.Single(r,x=>x.Error is not null).Error);await Success(target,r[0].Error is null?i1:i2,r.Single(x=>x.Error is null));var before=await Snapshot(o);Assert.Equal("revision_conflict",(await Update(s1,target,i1)).Error);Assert.Equal(before,await Snapshot(o));
    }
    [FunctionalTheory][InlineData("collectionEnvelope")][InlineData("numericString")][InlineData("unknown")][InlineData("duplicate")][InlineData("case")][InlineData("signature")][InlineData("revision")][InlineData("epoch")][InlineData("ownerPins")][InlineData("recipientPins")][InlineData("identity")]
    public async Task GivenMalformedContributingNativeEvidenceAlongsideGoodWriteGrant_WhenUpdating_Then503WithoutAnyWrite(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var b=await AssociationSetup.Items(fixture,o);await Members(o,a.Collection,[],[a.Folder.Id]);await Members(o,b.Collection,[],[a.Folder.Id]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);var grant=await AssociationSetup.Grant(fixture,o,owner,s,client,b.Collection);
        await using(var db=fixture.CreateContext()){if(kind=="collectionEnvelope")await db.Collections.Where(x=>x.Id==b.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,"bad"u8.ToArray()));if(kind is "numericString" or "unknown" or "duplicate" or "case")await db.Collections.Where(x=>x.Id==b.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,Malformed(b.Collection.Envelope,kind)));if(kind=="signature")await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));if(kind=="revision")await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Revision,2));if(kind=="epoch")await db.Collections.Where(x=>x.Id==b.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.KeyEpoch,2));if(kind is "ownerPins" or "recipientPins")await db.VaultProtections.Where(x=>x.AccountId==(kind=="ownerPins"?o.InternalId:s.InternalId)).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Material,"bad"u8.ToArray()));if(kind=="identity")await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,Bytes(owner.WrapGrant(o.AccountId,b.Collection.PublicId,grant.PublicId,Guid.NewGuid(),client.Material.RecipientKey))));}
        var before=await Snapshot(o);var recipient=await Snapshot(s);var r=await Update(s,a.Folder,Input());Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(o));Assert.Equal(recipient,await Snapshot(s));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenUnrelatedOrRevokedMalformedGrant_WhenUpdatingThroughGoodGrant_ThenIgnoreIrrelevantNativeEvidence(bool revoked)
    {using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var b=await AssociationSetup.Items(fixture,o);await Members(o,a.Collection,[],[a.Folder.Id]);await Members(o,b.Collection,[],revoked?[a.Folder.Id]:[]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);var g=await AssociationSetup.Grant(fixture,o,owner,s,client,b.Collection);await using(var db=fixture.CreateContext())await db.CollectionGrants.Where(x=>x.Id==g.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()).SetProperty(g=>g.State,revoked?CollectionGrantState.Revoked:CollectionGrantState.Active));var i=Input();await Success(a.Folder,i,await Update(s,a.Folder,i));}
    [FunctionalTheory][InlineData("account")][InlineData("session")][InlineData("profile")][InlineData("collection")][InlineData("grant")][InlineData("folder")]
    public async Task GivenNaturalExpiryDuringResourceLockWait_WhenUpdating_Then403BeforeConflictAndNoMutation(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);await Members(o,a.Collection,[],[a.Folder.Id]);var g=await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);var p=await Profile(s,client,key,collections:[a.Collection.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));var before=await Snapshot(o);
        await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();var id=kind switch{"account"=>s.InternalId,"profile"=>p.Id,"collection"=>a.Collection.Id,"grant"=>g.Id,_=>a.Folder.Id};var table=kind switch{"session"=>"vault_access_session","grant"=>"collection_grant",_=>kind};if(kind=="session")await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE handle_verifier={s.Verifier} FOR UPDATE");else {var sql=table switch{"account"=>"SELECT FROM cerberus.account WHERE id={0} FOR UPDATE","profile"=>"SELECT FROM cerberus.profile WHERE id={0} FOR UPDATE","collection"=>"SELECT FROM cerberus.collection WHERE id={0} FOR UPDATE","collection_grant"=>"SELECT FROM cerberus.collection_grant WHERE id={0} FOR UPDATE",_=>"SELECT FROM cerberus.folder WHERE id={0} FOR UPDATE"};await held.Database.ExecuteSqlRawAsync(sql,id);}
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));var pending=new FolderUpdateStore(new ProfileSetup.Factory(fixture,new Signal(reached,table))).UpdateAsync(new(s.Actor,s.Verifier,a.Folder.PublicId,Input(77)),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);await Past(deadline);await tx.CommitAsync();var r=await pending;Assert.Equal("vault_access_denied",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(o));
    }
    [FunctionalTheory][InlineData("revoke","not_found")][InlineData("downgrade","vault_access_denied")][InlineData("member","not_found")][InlineData("move","not_found")][InlineData("rotate","revision_conflict")]
    public async Task GivenPermissionOrRotationCommitsWhileCollectionLockBlocked_WhenUpdating_ThenReloadWinningState(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var child=await Folder(o,a.Folder.Id);var target=await Folder(o,child.Id);await Members(o,a.Collection,[],[a.Folder.Id]);var g=await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);
        await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE id={a.Collection.Id} FOR UPDATE");var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));var pending=new FolderUpdateStore(new ProfileSetup.Factory(fixture,new Signal(reached,"collection"))).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,Input()),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);
        if(kind=="revoke")await held.CollectionGrants.Where(x=>x.Id==g.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Revoked));if(kind=="downgrade")await held.CollectionGrants.Where(x=>x.Id==g.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Access,CollectionGrantAccess.ReadOnly));if(kind=="member")await held.CollectionFolders.Where(x=>x.CollectionId==a.Collection.Id).ExecuteDeleteAsync();if(kind=="move")await held.Folders.Where(x=>x.Id==child.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,(long?)null));
        if(kind=="rotate"){await held.Folders.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,Bytes(ProfileSetup.Envelope(2))).SetProperty(r=>r.Revision,2).SetProperty(r=>r.ConcurrencyStamp,Guid.NewGuid()));}
        await tx.CommitAsync();var before=await Snapshot(o);var r=await pending;Assert.Equal(error,r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(o));
    }
    [FunctionalTheory][InlineData("expiry","vault_access_denied")][InlineData("move","not_found")][InlineData("trash","not_found")][InlineData("terminal","not_found")]
    public async Task GivenScopeChangesImmediatelyBeforeUpdateStatement_WhenUpdating_ThenCurrentGuardDenies(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var child=await Folder(o,a.Folder.Id);var target=await Folder(o,child.Id);await Members(o,a.Collection,[],[a.Folder.Id]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);var deadline=DateTimeOffset.UtcNow.AddSeconds(2);if(kind=="expiry"){await using var db=fixture.CreateContext();await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));}
        var original=await Row(target.Id);var r=await new FolderUpdateStore(new ProfileSetup.Factory(fixture,new BeforeWrite(async()=>{await using var db=fixture.CreateContext();if(kind=="expiry")await Past(deadline);if(kind=="move")await db.Folders.Where(x=>x.Id==child.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,(long?)null));if(kind=="trash")await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.DeletedAt,DateTimeOffset.UtcNow));if(kind=="terminal"){db.TerminalErasures.Add(new(){ResourceId=target.PublicId,ResourceKind="folder",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}))).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,Input()),default);Assert.Equal(error,r.Error);Assert.Null(r.Data);Assert.Equal(JsonSerializer.Serialize(original),JsonSerializer.Serialize(await Row(target.Id)));
    }
    [FunctionalFact]
    public async Task GivenPostWriteFailure_WhenUpdating_ThenRollbackAndRetrySucceeds()
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Folder(s);var before=await Snapshot(s);var i=Input();var r=await new FolderUpdateStore(new ProfileSetup.Factory(fixture,new FailAfterWrite())).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,i),default);Assert.Equal("persistence_unavailable",r.Error);Assert.Equal(before,await Snapshot(s));await Success(target,i,await Update(s,target,i));}
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenExhaustedOrRegressedGlobalSequence_WhenUpdating_ThenRollback(bool exhausted)
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Folder(s);var before=await Snapshot(s);await using var db=fixture.CreateContext();var old=await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();try{await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{(exhausted?ProtocolBinary.MaxInteger:1)},true)");var r=await Update(s,target,Input());Assert.Equal(exhausted?"revision_conflict":"persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));}finally{await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{old},true)");}}
    [FunctionalFact]
    public async Task GivenUnavailableProviderOrCancelledCaller_WhenUpdating_ThenFailClosedOrPropagateCancellation()
    {var request=new FolderUpdateRequest(Guid.NewGuid(),new string('a',64),Guid.NewGuid(),Input());var dead=new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=absent;Username=none;Timeout=1;Pooling=false").Options);Assert.Equal("persistence_unavailable",(await new FolderUpdateStore(dead).UpdateAsync(request,default)).Error);using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().UpdateAsync(request,ct.Token));}


    [FunctionalTheory][InlineData(false,false)][InlineData(true,false)][InlineData(false,true)]
    public async Task GivenCyclicAncestry_WhenUpdating_Then503OnlyForRelevantActiveTarget(bool outside,bool hidden)
    {using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var root=await Folder(s);var child=await Folder(s,root.Id);var target=await Folder(s,child.Id);if(outside){var p=await Profile(s,owner,key);s=s with{Verifier=await Selected(s,p.Id)};}await using(var db=fixture.CreateContext())await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,child.Id).SetProperty(f=>f.DeletedAt,hidden?(DateTimeOffset?)DateTimeOffset.UtcNow:null));var before=await Snapshot(s);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(10));var r=await Store().UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,Input()),ct.Token);Assert.Equal(outside||hidden?"not_found":"persistence_unavailable",r.Error);Assert.Equal(before,await Snapshot(s));}
    [FunctionalFact]
    public async Task GivenNewNativeAuthorityImmediatelyBeforeWrite_WhenUpdating_ThenConflictRatherThanUseUnheldGrant()
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);await Members(o,a.Collection,[],[a.Folder.Id]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);var before=await Row(a.Folder.Id);
        var store=new FolderUpdateStore(new ProfileSetup.Factory(fixture,new BeforeWrite(async()=>{var b=await AssociationSetup.Items(fixture,o);await Members(o,b.Collection,[],[a.Folder.Id]);await AssociationSetup.Grant(fixture,o,owner,s,client,b.Collection,CollectionGrantAccess.ReadWrite);})));
        var r=await store.UpdateAsync(new(s.Actor,s.Verifier,a.Folder.PublicId,Input()),default);Assert.Equal("revision_conflict",r.Error);Assert.Null(r.Data);Assert.Equal(JsonSerializer.Serialize(before),JsonSerializer.Serialize(await Row(a.Folder.Id)));
    }
    [FunctionalFact]
    public async Task GivenReciprocalNativeWriteGrants_WhenBothActorsEditForeignFolders_ThenNoForeignAccountLockCycle()
    {
        using var one=new ProtectionFixture();using var two=new ProtectionFixture();var a=await ProfileSetup.Create(fixture,one);var b=await ProfileSetup.Create(fixture,two);var x=await AssociationSetup.Items(fixture,a);var y=await AssociationSetup.Items(fixture,b);await Members(a,x.Collection,[],[x.Folder.Id]);await Members(b,y.Collection,[],[y.Folder.Id]);await AssociationSetup.Grant(fixture,a,one,b,two,x.Collection,CollectionGrantAccess.ReadWrite);await AssociationSetup.Grant(fixture,b,two,a,one,y.Collection,CollectionGrantAccess.ReadWrite);
        var i=Input();var j=Input();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));var r=await Task.WhenAll(Store().UpdateAsync(new(a.Actor,a.Verifier,y.Folder.PublicId,i),ct.Token),Store().UpdateAsync(new(b.Actor,b.Verifier,x.Folder.PublicId,j),ct.Token));await Success(y.Folder,i,r[0]);await Success(x.Folder,j,r[1]);
    }
    [FunctionalFact]
    public async Task GivenCreationHoldsTargetBeforeAncestorLock_WhenRecipientEdits_ThenWaitWithoutCycleAndPreserveWinningParentRevision()
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var target=await Folder(o,a.Folder.Id);
        await Members(o,a.Collection,[],[a.Folder.Id]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var editing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var creator=new FolderCreateStore(new ProfileSetup.Factory(fixture,new PauseSecondFolder(reached,release))).CreateAsync(new(o.Actor,o.Verifier,new(Guid.NewGuid(),ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch,[],target.PublicId)),ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));var pending=new FolderUpdateStore(new ProfileSetup.Factory(fixture,new Signal(editing,"folder"))).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,Input()),ct.Token);
        try{await editing.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);}finally{release.TrySetResult();}
        Assert.Null((await creator).Error);var winning=await Row(target.Id);var result=await pending;Assert.Equal("revision_conflict",result.Error);Assert.Null(result.Data);Assert.Equal(target.Revision+1,winning.Revision);Assert.Equal(JsonSerializer.Serialize(winning),JsonSerializer.Serialize(await Row(target.Id)));Assert.Equal(target.Envelope,winning.Envelope);
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenOwnerNativeRotationAndRecipientEdit_WhenCompeting_ThenNeverOverwriteWinningCipher(bool rotationFirst)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);await Members(o,a.Collection,[],[a.Folder.Id]);var g=await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);
        var change=new ProtectionChange(o.AccountId,1,1,"rotate-content",owner.Rewrap(),[new("account",o.AccountId,1,ProfileSetup.Envelope(2)),new("record",a.Record.PublicId,1,ProfileSetup.Envelope(2)),new("folder",a.Folder.PublicId,1,ProfileSetup.Envelope(2)),new("collection",a.Collection.PublicId,1,ProfileSetup.Envelope(2))]){GrantReplacements=[new(g.PublicId,1,owner.WrapGrant(o.AccountId,a.Collection.PublicId,g.PublicId,s.Actor,client.Material.RecipientKey,2,2))]};
        var raw=Bytes(change);var challenge=(await new VaultProtectionStore(fixture).ChallengeAsync(o.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;var request=new ProtectionChangeRequest(o.Actor,o.Verifier,challenge.ChallengeId,owner.Sign(challenge),raw,change);
        if(rotationFirst){Assert.Null((await new VaultProtectionChangeStore(fixture).ChangeAsync(request,default)).Error);var before=await Snapshot(o);Assert.Equal("revision_conflict",(await Update(s,a.Folder,Input())).Error);Assert.Equal(before,await Snapshot(o));Assert.Equal(2,(await Row(a.Folder.Id)).Revision);}
        else{
            var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));var rotation=new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture,new PauseRotation(reached,release))).ChangeAsync(request,ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try{var i=Input();var r=await Store().UpdateAsync(new(s.Actor,s.Verifier,a.Folder.PublicId,i),ct.Token).WaitAsync(TimeSpan.FromSeconds(5));await Success(a.Folder,i,r);}finally{release.TrySetResult();}
            Assert.Equal("revision_conflict",(await rotation).Error);await using var db=fixture.CreateContext();Assert.False((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==challenge.ChallengeId)).Consumed);Assert.Equal(1,(await db.Collections.SingleAsync(x=>x.Id==a.Collection.Id)).KeyEpoch);Assert.Equal(2,(await Row(a.Folder.Id)).Revision);
        }
    }
    [FunctionalFact]
    public async Task GivenDeepUnrelatedInventories_WhenUpdatingOneSharedTarget_ThenAnalyzeOneCandidateAndOnlyItsAncestors()
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var child=await Folder(o,a.Folder.Id);var target=await Folder(o,child.Id);await Members(o,a.Collection,[],[target.Id]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);
        foreach(var account in new[]{o,s}){long? parent=null;for(var i=0;i<50;i++){var f=await Folder(account,parent);await Folder(account,f.Id);parent=f.Id;}}
        var capture=new Capture();var input=Input();var result=await new FolderUpdateStore(new ProfileSetup.Factory(fixture,capture)).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,input),default);await Success(target,input,result);
        var targetLock=Assert.Single(capture.Locks,x=>x.Contains("FROM cerberus.folder "));Assert.Contains("FOR NO KEY UPDATE",targetLock);Assert.Single(capture.Locks,x=>x.Contains("FROM cerberus.account "));Assert.Contains("WITH RECURSIVE",capture.Sql);Assert.Contains("WHERE r.public_id=@target",capture.Sql);
        await using var db=fixture.CreateContext();await db.Database.OpenConnectionAsync();await using var command=db.Database.GetDbConnection().CreateCommand();command.CommandText="EXPLAIN (ANALYZE, FORMAT JSON) "+capture.Sql;foreach(var p in capture.Parameters)command.Parameters.Add(p.Clone());using var plan=JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        static IEnumerable<JsonElement> Nodes(JsonElement node){yield return node;if(node.TryGetProperty("Plans",out var children))foreach(var child in children.EnumerateArray())foreach(var n in Nodes(child))yield return n;}
        var nodes=Nodes(plan.RootElement[0].GetProperty("Plan")).ToArray();Assert.Equal(1d,Assert.Single(nodes,x=>x.TryGetProperty("Subplan Name",out var n)&&n.GetString()=="CTE candidates").GetProperty("Actual Rows").GetDouble());Assert.Equal(3d,Assert.Single(nodes,x=>x.TryGetProperty("Subplan Name",out var n)&&n.GetString()=="CTE ancestry").GetProperty("Actual Rows").GetDouble());
    }
    private sealed class PauseSecondFolder(TaskCompletionSource reached,TaskCompletionSource release):DbCommandInterceptor
    {private int count;public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<int> r,CancellationToken ct=default){if(c.CommandText.Contains("FROM cerberus.folder ")&&c.CommandText.Contains("FOR UPDATE")&&++count==2){reached.TrySetResult();await release.Task.WaitAsync(ct);}return r;}}
    private sealed class PauseRotation(TaskCompletionSource reached,TaskCompletionSource release):DbCommandInterceptor
    {private bool used;public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> r,CancellationToken ct=default){if(!used&&c.CommandText.Contains("FROM cerberus.collection ")&&c.CommandText.Contains("FOR UPDATE")){used=true;reached.TrySetResult();await release.Task.WaitAsync(ct);}return r;}}
    private sealed class Capture:DbCommandInterceptor
    {
        public string Sql{get;private set;}="";public NpgsqlParameter[] Parameters{get;private set;}=[];public List<string> Locks{get;}=[];
        private void Note(DbCommand c){if(c.CommandText.Contains("FOR UPDATE")||c.CommandText.Contains("FOR NO KEY UPDATE")||c.CommandText.Contains("FOR SHARE"))Locks.Add(c.CommandText);}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<int> r,CancellationToken ct=default){Note(c);return ValueTask.FromResult(r);}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> r,CancellationToken ct=default){Note(c);if(Sql.Length==0&&c.CommandText.StartsWith("WITH RECURSIVE")){Sql=c.CommandText;Parameters=c.Parameters.Cast<NpgsqlParameter>().Select(p=>p.Clone()).ToArray();}return ValueTask.FromResult(r);}
    }

    [FunctionalTheory]
    [InlineData(CollectionGrantAccess.ReadOnly,false)]
    [InlineData(CollectionGrantAccess.ReadWrite,false)]
    [InlineData(CollectionGrantAccess.ReadOnly,true)]
    [InlineData(CollectionGrantAccess.ReadWrite,true)]
    public async Task GivenActualNativeRecordOnlyAuthority_WhenUpdatingContainingFolder_Then404DespiteSuccessfulRecordRead(CollectionGrantAccess access,bool selected)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();
        var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);
        await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));
        await Members(o,a.Collection,[a.Record.Id],[]);
        await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,access);
        if(selected){var p=await Profile(s,client,key,collections:[a.Collection.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};}
        var proof=await new ArturRios.Cerberus.Data.Records.RecordReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,a.Record.PublicId),default);
        Assert.Null(proof.Error);Assert.Equal(a.Record.PublicId,proof.Data!.Record.RecordId);
        await using(var db=fixture.CreateContext())await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.Envelope,"bad"u8.ToArray()));
        var before=await Snapshot(o);var recipient=await Snapshot(s);var result=await Update(s,a.Folder,Input(77));
        Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(o));Assert.Equal(recipient,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenSelectedProfileRecordOnlyAuthority_WhenUpdatingContainingFolder_Then404DespiteSuccessfulRecordRead()
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);
        await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));
        var p=await Profile(s,owner,key,records:[a.Record.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};
        var proof=await new ArturRios.Cerberus.Data.Records.RecordReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,a.Record.PublicId),default);
        Assert.Null(proof.Error);Assert.Equal(a.Record.PublicId,proof.Data!.Record.RecordId);
        var before=await Snapshot(s);var result=await Update(s,a.Folder,Input(77));Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("parent")][InlineData("sibling")]
    public async Task GivenNativeMemberLeafWriteAuthority_WhenUpdatingPrivateContainer_Then404AndNoChange(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);
        var leaf=await Folder(o,a.Folder.Id);var sibling=await Folder(o,a.Folder.Id);await Members(o,a.Collection,[],[leaf.Id]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);
        var proof=await new ArturRios.Cerberus.Data.Folders.FolderReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,leaf.PublicId),default);Assert.Null(proof.Error);Assert.Null(proof.Data!.ParentFolderId);
        var before=await Snapshot(o);var result=await Update(s,kind=="parent"?a.Folder:sibling,Input());Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(o));
    }
    [FunctionalTheory][InlineData("account")][InlineData("profile")][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("grant")]
    public async Task GivenTerminalMarkerWithSameGuidButDifferentKind_WhenUpdating_ThenOnlyFolderMarkerHidesTarget(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Folder(s);
        await using(var db=fixture.CreateContext()){db.TerminalErasures.Add(new(){ResourceId=target.PublicId,ResourceKind=kind,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var before=await Snapshot(s);var input=Input();var result=await Update(s,target,input);
        if(kind=="folder"){Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));}else await Success(target,input,result);
    }
    [FunctionalTheory][InlineData("profileTrash","vault_access_denied")][InlineData("profileTerminal","vault_access_denied")][InlineData("actorTerminal","not_found")]
    public async Task GivenSelectedScopeLifecycleRemoval_WhenUpdating_ThenFailWithoutWideningOrNullingSelection(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Folder(s);var p=await Profile(s,owner,key,folders:[target.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};
        await using(var db=fixture.CreateContext()){if(kind=="profileTrash")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));else{db.TerminalErasures.Add(new(){ResourceId=kind=="actorTerminal"?s.AccountId:p.PublicId,ResourceKind=kind=="actorTerminal"?"account":"profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        var before=await Snapshot(s);var result=await Update(s,target,Input());Assert.Equal(error,result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));await using var read=fixture.CreateContext();Assert.Equal(p.Id,(await read.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier)).ProfileId);
    }
    [FunctionalTheory][InlineData("cancel")][InlineData("timeout")][InlineData("db")][InlineData("dbUpdate")][InlineData("json")][InlineData("format")][InlineData("wrapped")]
    public async Task GivenNecessaryProviderFailure_WhenUpdating_Then503WithoutLeakingException(string kind)
    {
        Exception failure=kind switch{"cancel"=>new OperationCanceledException("private"),"timeout"=>new TimeoutException("private"),"db"=>new NpgsqlException("private"),"dbUpdate"=>new DbUpdateException("private"),"json"=>new JsonException("private"),"format"=>new FormatException("private"),_=>new InvalidOperationException("private",new TimeoutException("private"))};
        var result=await new FolderUpdateStore(new ThrowingFactory(failure)).UpdateAsync(new(Guid.NewGuid(),new string('a',64),Guid.NewGuid(),Input()),default);Assert.Equal("persistence_unavailable",result.Error);Assert.Null(result.Data);
    }
    private sealed class ThrowingFactory(Exception exception):IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()=>throw exception;
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromException<AppDbContext>(exception);
    }
    private FolderUpdateStore Store()=>new(fixture);
    private Task<VaultResult<FolderCreateDetails>> Update(ProfileSetup.State s,VaultFolder r,FolderUpdateInput i)=>Store().UpdateAsync(new(s.Actor,s.Verifier,r.PublicId,i),default);
    private static FolderUpdateInput Input(long revision=1)=>new(revision,ProfileSetup.Envelope(),DateTimeOffset.Parse("2026-10-09T00:00:00Z").AddTicks(19));
    private async Task Success(VaultFolder original,FolderUpdateInput input,VaultResult<FolderCreateDetails> result){Assert.Null(result.Error);var r=await Row(original.Id);Assert.Equal(original.PublicId,result.Data!.FolderId);Assert.Equal(original.Revision+1,result.Data.Revision);Assert.True(result.Data.ServerSequence>original.ServerSequence);Assert.Equal(input.EditedAt.AddTicks(-(input.EditedAt.Ticks%10)),result.Data.EditedAt);Assert.Equal(input.Envelope,JsonSerializer.Deserialize<EncryptedEnvelope>(r.Envelope,ProtectionFixture.Json));Assert.Equal(original.AccountId,r.AccountId);Assert.Equal(original.ParentFolderId,r.ParentFolderId);Assert.Equal(result.Data.Revision,r.Revision);Assert.Equal(result.Data.ServerSequence,r.ServerSequence);Assert.Equal(result.Data.EditedAt,r.EditedAt);Assert.NotEqual(original.ConcurrencyStamp,r.ConcurrencyStamp);}
    private async Task<VaultFolder> Row(long id){await using var db=fixture.CreateContext();return await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==id);}
    private async Task<VaultRecord> Record(ProfileSetup.State s,long? folder=null){var row=new VaultRecord{PublicId=Guid.NewGuid(),AccountId=s.InternalId,FolderId=folder,Envelope=Bytes(ProfileSetup.Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.CreateContext();db.Records.Add(row);await db.SaveChangesAsync();return row;}
    private async Task<VaultFolder> Folder(ProfileSetup.State s,long? parent=null){var row=new VaultFolder{PublicId=Guid.NewGuid(),AccountId=s.InternalId,ParentFolderId=parent,Envelope=Bytes(ProfileSetup.Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.CreateContext();db.Folders.Add(row);await db.SaveChangesAsync();return row;}
    private async Task<Profile> Profile(ProfileSetup.State s,ProtectionFixture owner,ProtectionFixture scoped,Guid[]? records=null,Guid[]? folders=null,Guid[]? collections=null){var input=ProfileSetup.Input(s,owner,scoped) with{RecordIds=records??[],FolderIds=folders??[],CollectionIds=collections??[]};Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);await using var db=fixture.CreateContext();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==input.ProfileId);}
    private async Task Members(ProfileSetup.State s,VaultCollection collection,long[] records,long[] folders){await using var db=fixture.CreateContext();db.CollectionRecords.AddRange(records.Select(x=>new CollectionRecord{AccountId=s.InternalId,CollectionId=collection.Id,RecordId=x}));db.CollectionFolders.AddRange(folders.Select(x=>new CollectionFolder{AccountId=s.InternalId,CollectionId=collection.Id,FolderId=x}));await db.SaveChangesAsync();}
    private async Task<string> Selected(ProfileSetup.State s,long profile){var verifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));await using var db=fixture.CreateContext();db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,ProfileId=profile,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return verifier;}
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private async Task<string> Snapshot(ProfileSetup.State s){await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),ProfileRecords=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync(),ProfileFolders=await db.ProfileFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileCollections=await db.ProfileCollections.AsNoTracking().Where(x=>db.Profiles.Any(p=>p.Id==x.ProfileId && p.AccountId==s.InternalId)).OrderBy(x=>x.ProfileId).ThenBy(x=>x.CollectionId).ToArrayAsync(),CollectionRecords=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),CollectionFolders=await db.CollectionFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.FolderId).ToArrayAsync(),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>x.RecipientAccountId==s.InternalId || db.Collections.Any(c=>c.Id==x.CollectionId && c.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync()});}

    private async Task<string> Unrelated(ProfileSetup.State s,long folder)
    {
        var node=System.Text.Json.Nodes.JsonNode.Parse(await Snapshot(s))!.AsObject();var rows=node["Folders"]!.AsArray();node["Folders"]=new System.Text.Json.Nodes.JsonArray(rows.Where(r=>r!["Id"]!.GetValue<long>()!=folder).Select(r=>r!.DeepClone()).ToArray());return node.ToJsonString();
    }
    private static byte[] Malformed(byte[] raw,string kind){var s=Encoding.UTF8.GetString(raw);return Encoding.UTF8.GetBytes(kind switch{"numericString"=>s.Replace("\"keyEpoch\":1","\"keyEpoch\":\"1\""),"unknown"=>s.Replace("{","{\"secret\":1,"),"duplicate"=>s.Replace("{","{\"keyEpoch\":1,"),_=>s.Replace("\"format\"","\"Format\"")});}
    private static async Task Past(DateTimeOffset deadline){var delay=deadline-DateTimeOffset.UtcNow;if(delay>TimeSpan.Zero)await Task.Delay(delay+TimeSpan.FromMilliseconds(80));}
    private static bool IsWrite(DbCommand c)=>c.CommandText.Contains("UPDATE cerberus.folder AS");
    private sealed class Signal(TaskCompletionSource reached,string table):DbCommandInterceptor
    {
        private void Check(DbCommand c){if(c.CommandText.Contains("cerberus."+table)&& (c.CommandText.Contains("FOR UPDATE")||c.CommandText.Contains("FOR NO KEY UPDATE")||c.CommandText.Contains("FOR SHARE")))reached.TrySetResult();}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<int> r,CancellationToken ct=default){Check(c);return ValueTask.FromResult(r);}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> r,CancellationToken ct=default){Check(c);return ValueTask.FromResult(r);}
    }
    private sealed class BeforeWrite(Func<Task> action):DbCommandInterceptor
    {private bool used;public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<int> r,CancellationToken ct=default){if(IsWrite(c)&&!used){used=true;await action();}return r;}}
    private sealed class FailAfterWrite:DbCommandInterceptor
    {private bool wrote;public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData e,int r,CancellationToken ct=default){if(IsWrite(c)&&r>0)wrote=true;return ValueTask.FromResult(r);}public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> r,CancellationToken ct=default){if(wrote)throw new TimeoutException("synthetic postwrite outage");return ValueTask.FromResult(r);}}
}
