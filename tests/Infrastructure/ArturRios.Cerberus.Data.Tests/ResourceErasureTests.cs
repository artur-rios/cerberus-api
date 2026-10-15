using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public partial class ResourceErasureTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Deleted=DateTimeOffset.Parse("2026-10-14T00:00:00Z");
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenSameUuidAcrossFourNativeKinds_WhenReplayingOneErasure_ThenOnlyExactTypedCipherIsPhysicallyRemoved(string kind)
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var id=Guid.NewGuid();await AssociationSetup.Items(fixture,s,id);
        var input=new ProfileCreateInput(id,ProfileSetup.Envelope(),new("Master",scoped.Material.UnlockVerifier,keys.Wrap(s.AccountId,"profile",id,s.Actor),null),DateTimeOffset.UnixEpoch,[],[],[]);
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);
        var before=new Dictionary<string,string>();foreach(var k in Kinds.Where(x=>x!=kind))before[k]=(await Read(k,id)).GetRawText();var protectedState=await TrashRestoreSetup.Protected(fixture,s);
        var store=new TerminalErasureStore(fixture);await store.ReapplyAsync(new(id,kind,Deleted),default);Assert.False(await Exists(kind,id));
        foreach(var pair in before)Assert.Equal(pair.Value,(await Read(pair.Key,id)).GetRawText());Assert.Equal(protectedState,await TrashRestoreSetup.Protected(fixture,s));
        Assert.True(await store.IsErasedAsync(kind,id,default));foreach(var k in Kinds.Where(x=>x!=kind))Assert.False(await store.IsErasedAsync(k,id,default));
        await store.ReapplyAsync(new(id,kind,Deleted.AddMinutes(1)),default);await using var db=fixture.CreateContext();Assert.Equal(Deleted,(await db.TerminalErasures.SingleAsync(x=>x.ResourceKind==kind&&x.ResourceId==id)).DeletedAt);
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenParentLedgerBeforeChildren_WhenReplayingFolder_ThenDetachButNeverRecursivelyEraseUnmarkedSurvivors(bool childTerminal)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var a=await AssociationSetup.Items(fixture,s);
        var child=new VaultFolder{AccountId=s.InternalId,PublicId=Guid.NewGuid(),Envelope=a.Folder.Envelope,EditedAt=a.Folder.EditedAt,ParentFolderId=a.Folder.Id};
        await using(var db=fixture.CreateContext()){db.Folders.Add(child);await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));
            if(childTerminal)db.TerminalErasures.Add(new(){ResourceKind="record",ResourceId=a.Record.PublicId,DeletedAt=Deleted});await db.SaveChangesAsync();}
        var recordBefore=await Read("record",a.Record.PublicId);var folderBefore=await Read("folder",child.PublicId);var other=await Read("collection",a.Collection.PublicId);
        var store=new TerminalErasureStore(fixture);await store.ReapplyAsync(new(a.Folder.PublicId,"folder",Deleted),default);
        Assert.False(await Exists("folder",a.Folder.PublicId));PreservedChild(recordBefore,await Read("record",a.Record.PublicId),"folder_id");PreservedChild(folderBefore,await Read("folder",child.PublicId),"parent_folder_id");Assert.Equal(other.GetRawText(),(await Read("collection",a.Collection.PublicId)).GetRawText());
        if(childTerminal){await store.ReapplyAsync(new(a.Record.PublicId,"record",Deleted),default);Assert.False(await Exists("record",a.Record.PublicId));}else Assert.False(await store.IsErasedAsync("record",a.Record.PublicId,default));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenOldBackupProfileSession_WhenReplayingProfile_ThenRevokeWithoutClearingSelectionOrChangingWideSession(bool alreadyRevoked)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var p=await AssociationSetup.Profile(fixture,s,keys);
        var verifier=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");Guid id;
        await using(var db=fixture.CreateContext()){id=await db.Profiles.Where(x=>x.Id==p).Select(x=>x.PublicId).SingleAsync();db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,ProfileId=p,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1,Revoked=alreadyRevoked});await db.SaveChangesAsync();}
        await using var check=fixture.CreateContext();var wide=JsonSerializer.Serialize(await check.VaultAccessSessions.AsNoTracking().SingleAsync(x=>x.HandleVerifier==s.Verifier));
        await new TerminalErasureStore(fixture).ReapplyAsync(new(id,"profile",Deleted),default);Assert.False(await Exists("profile",id));
        var selected=await check.VaultAccessSessions.AsNoTracking().SingleAsync(x=>x.HandleVerifier==verifier);Assert.Equal(p,selected.ProfileId);Assert.True(selected.Revoked);Assert.Equal(wide,JsonSerializer.Serialize(await check.VaultAccessSessions.AsNoTracking().SingleAsync(x=>x.HandleVerifier==s.Verifier)));
    }
    [FunctionalFact]
    public async Task GivenNativeSharedCollectionInOldBackup_WhenReplayingItsErasure_ThenRemoveGrantAndLinksButKeepAllActiveMembersAndForeignContent()
    {
        using var keys=new ProtectionFixture();using var recipientKeys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var recipient=await ProfileSetup.Create(fixture,recipientKeys);var a=await AssociationSetup.Items(fixture,s);var foreign=await AssociationSetup.Items(fixture,recipient);var profile=await AssociationSetup.Profile(fixture,recipient,recipientKeys);
        var grant=await AssociationSetup.Grant(fixture,s,keys,recipient,recipientKeys,a.Collection);
        await using(var db=fixture.CreateContext()){db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,RecordId=a.Record.Id});db.CollectionFolders.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,FolderId=a.Folder.Id});db.ProfileCollections.Add(new(){ProfileId=profile,CollectionId=a.Collection.Id});await db.SaveChangesAsync();}
        var before=new[]{(await Read("record",a.Record.PublicId)).GetRawText(),(await Read("folder",a.Folder.PublicId)).GetRawText(),(await Read("record",foreign.Record.PublicId)).GetRawText(),(await Read("folder",foreign.Folder.PublicId)).GetRawText(),(await Read("collection",foreign.Collection.PublicId)).GetRawText(),await TrashRestoreSetup.Protected(fixture,recipient)};
        await new TerminalErasureStore(fixture).ReapplyAsync(new(a.Collection.PublicId,"collection",Deleted),default);Assert.False(await Exists("collection",a.Collection.PublicId));
        await using var check=fixture.CreateContext();Assert.False(await check.CollectionGrants.AnyAsync(x=>x.Id==grant.Id));Assert.False(await check.ProfileCollections.AnyAsync(x=>x.ProfileId==profile&&x.CollectionId==a.Collection.Id));
        var after=new[]{(await Read("record",a.Record.PublicId)).GetRawText(),(await Read("folder",a.Folder.PublicId)).GetRawText(),(await Read("record",foreign.Record.PublicId)).GetRawText(),(await Read("folder",foreign.Folder.PublicId)).GetRawText(),(await Read("collection",foreign.Collection.PublicId)).GetRawText(),await TrashRestoreSetup.Protected(fixture,recipient)};Assert.Equal(before,after);
    }
    [FunctionalTheory][InlineData("record",false)][InlineData("folder",false)][InlineData("collection",false)][InlineData("profile",false)]
    [InlineData("record",true)][InlineData("folder",true)][InlineData("collection",true)][InlineData("profile",true)]
    public async Task GivenFailureAfterPhysicalMutation_WhenReplaying_ThenRollbackMarkerAndCipherAndPropagateCancellation(string kind,bool cancel)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);var before=await TrashRestoreSetup.Snapshot(fixture,s);using var ct=new CancellationTokenSource();
        var hook=new AfterDelete(kind,()=>{if(cancel){ct.Cancel();throw new OperationCanceledException(ct.Token);}throw new TimeoutException("fixture post-delete failure");});
        var store=new TerminalErasureStore(new ProfileSetup.Factory(fixture,hook));
        if(cancel)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.ReapplyAsync(new(root.ResourceId,kind,Deleted),ct.Token));else await Assert.ThrowsAsync<TimeoutException>(()=>store.ReapplyAsync(new(root.ResourceId,kind,Deleted),default));
        Assert.True(hook.Reached);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));await using var check=fixture.CreateContext();Assert.False(await check.TerminalErasures.AnyAsync(x=>x.ResourceKind==kind&&x.ResourceId==root.ResourceId));
        await new TerminalErasureStore(fixture).ReapplyAsync(new(root.ResourceId,kind,Deleted),default);Assert.False(await Exists(kind,root.ResourceId));
    }
    private static readonly string[] Kinds=["record","folder","collection","profile"];
    private async Task<JsonElement> Read(string kind,Guid id)=>await TrashRestoreSetup.Resource(fixture,new(kind,id,0,Guid.Empty,0,0,default,default,default));
    private async Task<bool> Exists(string kind,Guid id){await using var db=fixture.CreateContext();return kind switch{"record"=>await db.Records.AnyAsync(x=>x.PublicId==id),"folder"=>await db.Folders.AnyAsync(x=>x.PublicId==id),"collection"=>await db.Collections.AnyAsync(x=>x.PublicId==id),_=>await db.Profiles.AnyAsync(x=>x.PublicId==id)};}
    private static void PreservedChild(JsonElement before,JsonElement after,string parent)
    {Assert.Equal(JsonValueKind.Null,after.GetProperty(parent).ValueKind);Assert.Equal(before.GetProperty("revision").GetInt64()+1,after.GetProperty("revision").GetInt64());Assert.True(after.GetProperty("server_sequence").GetInt64()>before.GetProperty("server_sequence").GetInt64());foreach(var p in before.EnumerateObject().Where(x=>x.Name!=parent&&x.Name is not("revision" or "server_sequence" or "concurrency_stamp")))Assert.Equal(p.Value.GetRawText(),after.GetProperty(p.Name).GetRawText());}
    private sealed class AfterDelete(string kind,Action action):DbCommandInterceptor
    {
        internal bool Reached{get;private set;}
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default)
        {if(!Reached&&c.CommandText.StartsWith("DELETE FROM cerberus."+kind+" ",StringComparison.Ordinal)){Reached=true;action();}return ValueTask.FromResult(r);}
    }
}
