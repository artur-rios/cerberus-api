using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class TrashListStoreTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenFourActualDeletionKinds_WhenListing_ThenOriginalNativeRootsAndNoMutation()
    {
        using var keys=new ProtectionFixture(); using var scoped=new ProtectionFixture(); var s=await ProfileSetup.Create(fixture,keys);
        var a=await AssociationSetup.Items(fixture,s); var p=await Profile(s,keys,scoped);
        var record=await new RecordTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,a.Record.PublicId,1),default);Assert.Null(record.Error);
        var folder=await new FolderTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,a.Folder.PublicId,1),default);Assert.Null(folder.Error);
        var collection=await new CollectionTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,a.Collection.PublicId,1),default);Assert.Null(collection.Error);
        var profile=await new ProfileTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,p.PublicId,1),default);Assert.Null(profile.Error);
        var before=await Snapshot(s);var result=await List(s);Assert.Null(result.Error);Assert.Equal(4,result.Data!.Items.Count);
        Assert.Equal(new[]{"collection","folder","profile","record"},result.Data.Items.Select(x=>x.Kind).Order());
        foreach(var row in result.Data.Items){Assert.Equal(TimeSpan.FromDays(30),row.PurgeAt-row.DeletedAt);Assert.True(row.Revision>1);Assert.NotEmpty(row.Envelope);}
        Assert.Equal(a.Record.Envelope,result.Data.Items.Single(x=>x.Kind=="record").Envelope);
        Assert.NotNull(result.Data.Items.Single(x=>x.Kind=="profile").KeyWrappers);Assert.Equal(1,result.Data.Items.Single(x=>x.Kind=="collection").KeyEpoch);
        Assert.False(result.Data.HasMore);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenFolderCascadeAndIndependentPriorTrash_WhenListing_ThenOneRootPerOriginalOperation()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var a=await AssociationSetup.Items(fixture,s);
        await using(var db=fixture.CreateContext()){await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));}
        var prior=await new RecordTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,a.Record.PublicId,1),default);Assert.Null(prior.Error);
        var child=new VaultRecord{AccountId=s.InternalId,FolderId=a.Folder.Id,Envelope=a.Record.Envelope,EditedAt=a.Record.EditedAt};
        await using(var db=fixture.CreateContext()){db.Records.Add(child);await db.SaveChangesAsync();}
        await using(var db=fixture.CreateContext())a=a with{Folder=await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==a.Folder.Id)};
        var cascade=await new FolderTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,a.Folder.PublicId,a.Folder.Revision),default);Assert.Null(cascade.Error);
        var r=await List(s);Assert.Null(r.Error);Assert.Equal(2,r.Data!.Items.Count);Assert.DoesNotContain(r.Data.Items,x=>x.ResourceId==child.PublicId);
        Assert.Equal(prior.Data!.PurgeAt,r.Data.Items.Single(x=>x.Kind=="record").PurgeAt);
        Assert.Equal(cascade.Data!.TrashOperationId,r.Data.Items.Single(x=>x.Kind=="folder").OperationId);
    }
    [FunctionalTheory][InlineData("direct",true)][InlineData("collection",true)][InlineData("folder",true)]
    [InlineData("unselected",false)][InlineData("collectionTrash",false)][InlineData("parentTrash",false)]
    [InlineData("directMissingParent",true)][InlineData("foreignCollection",false)][InlineData("badSnapshot",false)]
    public async Task GivenRetainedScope_WhenListingSelected_ThenOnlyCurrentOwnedRootAuthority(string route,bool visible)
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);
        var a=await AssociationSetup.Items(fixture,s);var p=await Profile(s,keys,scoped);
        await using(var db=fixture.CreateContext()){
            if(route is "direct" or "directMissingParent")db.ProfileRecords.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,RecordId=a.Record.Id});
            if(route is "collection" or "collectionTrash" or "foreignCollection"){db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=a.Collection.Id});db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,RecordId=a.Record.Id});}
            if(route is "folder" or "parentTrash" or "directMissingParent"){
                await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));
                if(route!="directMissingParent")db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,FolderId=a.Folder.Id});}
            await db.SaveChangesAsync();}
        Assert.Null((await new RecordTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,a.Record.PublicId,1),default)).Error);
        await using(var db=fixture.CreateContext()){
            if(route=="collectionTrash")await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.DeletedAt,DateTimeOffset.UtcNow));
            if(route is "parentTrash" or "directMissingParent")await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.DeletedAt,DateTimeOffset.UtcNow));
            if(route=="foreignCollection")await db.ProfileCollections.Where(x=>x.ProfileId==p.Id).ExecuteDeleteAsync();
            if(route=="badSnapshot")await db.TrashEntries.Where(x=>x.ResourceId==a.Record.PublicId).ExecuteUpdateAsync(x=>x.SetProperty(e=>e.AssociationSnapshot,"bad"u8.ToArray()));
            await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,p.Id));}
        var before=await Snapshot(s);var r=await List(s);Assert.Null(r.Error);Assert.Equal(visible?1:0,r.Data!.Items.Count);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("expired")][InlineData("terminal")][InlineData("missingRoot")][InlineData("foreign")]
    public async Task GivenUnrecoverableOrForeignTrash_WhenListing_ThenOmitBeforePagination(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var other=await ProfileSetup.Create(fixture,keys);
        var hidden=await Trashed(kind=="foreign"?other:s);var visible=await Trashed(s);
        await using(var db=fixture.CreateContext()){
            if(kind=="expired")await db.TrashOperations.Where(x=>x.PublicId==hidden.OperationId).ExecuteUpdateAsync(x=>x.SetProperty(o=>o.PurgeAt,DateTimeOffset.UtcNow.AddMinutes(-1)));
            if(kind=="terminal")db.TerminalErasures.Add(new(){ResourceKind="record",ResourceId=hidden.ResourceId,DeletedAt=DateTimeOffset.UtcNow});
            if(kind=="missingRoot")await db.Records.Where(x=>x.PublicId==hidden.ResourceId).ExecuteDeleteAsync();await db.SaveChangesAsync();}
        var before=await Snapshot(s);var r=await List(s,1);Assert.Null(r.Error);Assert.Equal(visible.ResourceId,Assert.Single(r.Data!.Items).ResourceId);Assert.False(r.Data.HasMore);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenDifferentKindTerminalWithSameGuid_WhenListing_ThenRetainOwnedRecord()
    {using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var item=await Trashed(s);await using(var db=fixture.CreateContext()){db.TerminalErasures.Add(new(){ResourceKind="folder",ResourceId=item.ResourceId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}var r=await List(s);Assert.Null(r.Error);Assert.Equal(item.ResourceId,Assert.Single(r.Data!.Items).ResourceId);}
    [FunctionalFact]
    public async Task GivenNewDeletionAndRevocationBetweenPages_WhenContinuing_ThenBoundNewOperationsAndRecheckAuthority()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var first=await Trashed(s);var second=await Trashed(s);
        var initial=(await List(s,1)).Data!;Assert.True(initial.HasMore);Assert.Equal(first.ResourceId,Assert.Single(initial.Items).ResourceId);await Trashed(s);
        var page=await Store().ListAsync(new(s.Actor,s.Verifier,1,initial.Items[0].Position,initial.Boundary),default);Assert.Null(page.Error);Assert.Equal(second.ResourceId,Assert.Single(page.Data!.Items).ResourceId);Assert.False(page.Data.HasMore);
        await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revoked,true));
        Assert.Equal("vault_access_denied",(await Store().ListAsync(new(s.Actor,s.Verifier,1,initial.Items[0].Position,initial.Boundary),default)).Error);
    }
    [FunctionalTheory][InlineData("missing","not_found")][InlineData("closing","not_found")][InlineData("terminal","not_found")]
    [InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")]
    [InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")]
    [InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")]
    [InlineData("nullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)]
    public async Task GivenInvalidCurrentAuthority_WhenListing_ThenFailClosed(string kind,string? error)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);await Trashed(s);
        await using(var db=fixture.CreateContext()){var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);
            if(kind=="closing")a.State=AccountState.ClosurePending;if(kind=="terminal")db.TerminalErasures.Add(new(){ResourceKind="account",ResourceId=s.AccountId,DeletedAt=DateTimeOffset.UtcNow});
            if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);
            if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="zeroPolicy")v.PolicyRevision=0;if(kind=="zeroGeneration")v.RevocationGeneration=0;
            if(kind is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(kind is "nullExpiry" or "noExpiry")v.ExpiresAt=null;await db.SaveChangesAsync();}
        var before=await Snapshot(s);var r=await Store().ListAsync(new(kind=="missing"?Guid.NewGuid():s.Actor,kind=="access"?new string('a',64):s.Verifier,10,0,null),default);
        Assert.Equal(error,r.Error);if(error is not null)Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenEmptyTrash_WhenListing_ThenEmptyFinalPage()
    {using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var r=await List(s);Assert.Null(r.Error);Assert.Empty(r.Data!.Items);Assert.Equal(0,r.Data.Boundary);Assert.False(r.Data.HasMore);}
    [FunctionalFact]
    public async Task GivenCancelledCaller_WhenListing_ThenPropagateCancellation()
    {using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().ListAsync(new(Guid.NewGuid(),"a",1,0,null),ct.Token));}
    [FunctionalTheory][InlineData("profile")][InlineData("collection")][InlineData("folder")]
    public async Task GivenSelectedOwnerAndTrashedRoot_WhenListing_ThenRetainedExplicitProfileScope(string kind)
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);
        var a=await AssociationSetup.Items(fixture,s);var p=await Profile(s,keys,scoped);
        await using(var db=fixture.CreateContext()){
            db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,FolderId=a.Folder.Id});
            db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=a.Collection.Id});await db.SaveChangesAsync();}
        if(kind=="folder")Assert.Null((await new FolderTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,a.Folder.PublicId,1),default)).Error);
        if(kind=="collection")Assert.Null((await new CollectionTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,a.Collection.PublicId,1),default)).Error);
        if(kind=="profile"){
            using var otherScoped=new ProtectionFixture();var other=await Profile(s,keys,otherScoped);
            Assert.Null((await new ProfileTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,other.PublicId,1),default)).Error);}
        await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,p.Id));
        var r=await List(s);Assert.Null(r.Error);if(kind=="profile")Assert.Empty(r.Data!.Items);else Assert.Equal(kind,Assert.Single(r.Data!.Items).Kind);
    }
    [FunctionalTheory][InlineData("signature")][InlineData("pins")][InlineData("revision")][InlineData("epoch")]
    public async Task GivenCorruptVisibleProfileBinding_WhenListing_ThenUnavailableWithoutCiphertext(string damage)
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var p=await Profile(s,keys,scoped);
        Assert.Null((await new ProfileTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,p.PublicId,1),default)).Error);
        await using(var db=fixture.CreateContext()){
            if(damage=="pins")await db.VaultProtections.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Material,"bad"u8.ToArray()));
            else {var row=await db.Profiles.SingleAsync(x=>x.Id==p.Id);var w=JsonSerializer.Deserialize<ProfileKeyWrappers>(row.KeyWrappers,ProtectionFixture.Json)!;
                w=w with {MasterKeyWrapper=w.MasterKeyWrapper with {Signature=damage=="signature"?new string('A',86):w.MasterKeyWrapper.Signature,
                    GrantRevision=damage=="revision"?3:1,KeyEpoch=damage=="epoch"?2:1}};row.KeyWrappers=JsonSerializer.SerializeToUtf8Bytes(w,ProtectionFixture.Json);await db.SaveChangesAsync();}}
        var before=await Snapshot(s);var r=await List(s);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("account",false)][InlineData("session",false)][InlineData("account",true)][InlineData("session",true)]
    public async Task GivenExpiryDuringLockWait_WhenListing_ThenRecheckDatabaseTimeBeforeRelease(string table,bool trashExpiry)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var item=await Trashed(s);
        var deadline=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds());
        await using(var db=fixture.CreateContext()){
            if(trashExpiry){await db.TrashOperations.Where(x=>x.PublicId==item.OperationId).ExecuteUpdateAsync(x=>x.SetProperty(o=>o.DeletedAt,deadline.AddDays(-30)).SetProperty(o=>o.PurgeAt,deadline));
                await db.Records.Where(x=>x.PublicId==item.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,deadline.AddDays(-30)).SetProperty(r=>r.PurgeAt,deadline));}
            else await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));}
        var before=await Snapshot(s);await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();
        if(table=="account")await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
        else await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE handle_verifier={s.Verifier} FOR UPDATE");
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending=new TrashListStore(new ProfileSetup.Factory(fixture,new Signal(reached,table))).ListAsync(new(s.Actor,s.Verifier,50,0,null),ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);
        var delay=deadline-DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(80);if(delay>TimeSpan.Zero)await Task.Delay(delay,ct.Token);
        await tx.CommitAsync();var result=await pending;
        if(trashExpiry){Assert.Null(result.Error);Assert.Empty(result.Data!.Items);}else {Assert.Equal("vault_access_denied",result.Error);Assert.Null(result.Data);}
        Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("cycle",true)][InlineData("hiddenCycle",false)][InlineData("missingParent",false)]
    public async Task GivenRequiredFormerParentAncestry_WhenListing_ThenCompleteActivePathOrFailClosed(string kind,bool unavailable)
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var a=await AssociationSetup.Items(fixture,s);var p=await Profile(s,keys,scoped);
        await using(var db=fixture.CreateContext()){await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,FolderId=a.Folder.Id});await db.SaveChangesAsync();}
        Assert.Null((await new RecordTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,a.Record.PublicId,1),default)).Error);
        await using(var db=fixture.CreateContext()){
            if(kind=="missingParent")await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteDeleteAsync();
            else {await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,a.Folder.Id));if(kind=="hiddenCycle")await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.DeletedAt,DateTimeOffset.UtcNow));}
            await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,p.Id));}
        var r=await List(s);Assert.Equal(unavailable?"persistence_unavailable":null,r.Error);if(!unavailable)Assert.Empty(r.Data!.Items);else Assert.Null(r.Data);
    }
    [FunctionalTheory][InlineData("duplicate")][InlineData("unknown")][InlineData("case")][InlineData("numeric")][InlineData("invalid")][InlineData("upperGuid")][InlineData("null")]
    public async Task GivenUnusableRetainedAuthorityWithUnrelatedVisibleRoot_WhenListing_ThenOmitWithoutBroadeningOrPartialFailure(string damage)
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var p=await Profile(s,keys,scoped);
        var hidden=await Trashed(s);var a=await AssociationSetup.Items(fixture,s);
        await using(var db=fixture.CreateContext()){db.ProfileRecords.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,RecordId=a.Record.Id});await db.SaveChangesAsync();}
        Assert.Null((await new RecordTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,a.Record.PublicId,1),default)).Error);
        var valid=JsonSerializer.Serialize(new RecordAssociationSnapshot(null,[p.PublicId],[]),ProtectionFixture.Json);
        var bad=damage switch{"duplicate"=>valid.Replace("{","{\"profileIds\":[],"),"unknown"=>valid.Replace("{","{\"extra\":[],"),"case"=>valid.Replace("profileIds","ProfileIds"),"numeric"=>valid.Replace("[]","1"),"upperGuid"=>valid.Replace(p.PublicId.ToString(),p.PublicId.ToString().ToUpperInvariant()),"null"=>"null",_=>"bad"};
        await using(var db=fixture.CreateContext()){await db.TrashEntries.Where(x=>x.ResourceId==hidden.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(e=>e.AssociationSnapshot,System.Text.Encoding.UTF8.GetBytes(bad)));await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,p.Id));}
        var r=await List(s);Assert.Null(r.Error);Assert.Equal(a.Record.PublicId,Assert.Single(r.Data!.Items).ResourceId);
    }
    [FunctionalFact]
    public async Task GivenUnavailableRequiredPersistence_WhenListing_ThenStableUnavailableWithoutMutation()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var before=await Snapshot(s);
        var r=await new TrashListStore(new ProfileSetup.Factory(fixture,new Failure())).ListAsync(new(s.Actor,s.Verifier,50,0,null),default);
        Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));
    }
    private sealed class Signal(TaskCompletionSource reached,string table):DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default)
        {if(c.CommandText.Contains("cerberus."+(table=="session"?"vault_access_session":table),StringComparison.Ordinal)&&c.CommandText.Contains("FOR SHARE",StringComparison.Ordinal))reached.TrySetResult();return ValueTask.FromResult(r);}
    }
    private sealed class Failure:DbCommandInterceptor
    {public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default)=>throw new NpgsqlException("fixture private persistence details");}

    private TrashListStore Store()=>new(fixture);
    private Task<VaultResult<TrashListPage>> List(ProfileSetup.State s,int size=50)=>Store().ListAsync(new(s.Actor,s.Verifier,size,0,null),default);
    private async Task<Profile> Profile(ProfileSetup.State s,ProtectionFixture keys,ProtectionFixture scoped)
    {var input=ProfileSetup.Input(s,keys,scoped);Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);await using var db=fixture.CreateContext();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==input.ProfileId);}
    private async Task<(Guid ResourceId,Guid OperationId)> Trashed(ProfileSetup.State s)
    {var a=await AssociationSetup.Items(fixture,s);var r=await new RecordTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,a.Record.PublicId,1),default);Assert.Null(r.Error);return(a.Record.PublicId,r.Data!.TrashOperationId);}
    private async Task<string> Snapshot(ProfileSetup.State s)
    {await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Operations=await db.TrashOperations.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync()},ProtectionFixture.Json);}
}
