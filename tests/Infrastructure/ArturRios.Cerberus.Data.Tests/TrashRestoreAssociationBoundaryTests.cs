using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace ArturRios.Cerberus.Data.Tests;

public partial class TrashRestoreAssociationTests
{
    [FunctionalTheory][InlineData("wrappers")][InlineData("signature")][InlineData("pins")]
    public async Task GivenCorruptRequiredProfileNativeEvidence_WhenRestoring_Then503AndExactRollback(string damage)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"profile");
        await using(var db=fixture.CreateContext()){
            if(damage=="wrappers")await db.Profiles.Where(x=>x.Id==root.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.KeyWrappers,"bad"u8.ToArray()));
            if(damage=="signature"){
                var p=await db.Profiles.SingleAsync(x=>x.Id==root.InternalId);var node=System.Text.Json.Nodes.JsonNode.Parse(p.KeyWrappers)!;
                node["masterKeyWrapper"]!["signature"]=ProtocolBinary.Encode(new byte[64]);p.KeyWrappers=JsonSerializer.SerializeToUtf8Bytes(node);await db.SaveChangesAsync();}
            if(damage=="pins")await db.VaultProtections.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Material,"bad"u8.ToArray()));}
        var before=await TrashRestoreSetup.Snapshot(fixture,s);Assert.Equal("persistence_unavailable",(await Restore(s,root)).Error);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenCurrentForeignCollectionLinkWithInvalidNativeBinding_WhenRestoringProfile_Then503UnlessRevoked(bool revoked)
    {
        using var keys=new ProtectionFixture();using var author=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var other=await ProfileSetup.Create(fixture,author);var a=await AssociationSetup.Items(fixture,other);var p=await Profile(s,keys,scoped);
        var grant=await AssociationSetup.Grant(fixture,other,author,s,keys,a.Collection);
        await using(var db=fixture.CreateContext()){db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=a.Collection.Id});await db.SaveChangesAsync();}
        var root=await TrashRestoreSetup.TrashAsync(fixture,s,"profile",p.PublicId,p.Revision,p.Id,p.EditedAt);
        await using(var db=fixture.CreateContext()){await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()).SetProperty(g=>g.State,revoked?CollectionGrantState.Revoked:CollectionGrantState.Active));}
        var before=await TrashRestoreSetup.Snapshot(fixture,s);var r=await Restore(s,root);
        if(revoked){Assert.Null(r.Error);Assert.Contains(r.Data!.Omissions,x=>x.AssociationKind=="collection"&&x.Count==1);}else{Assert.Equal("persistence_unavailable",r.Error);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));}
    }
    [FunctionalTheory][InlineData("max","revision_conflict")][InlineData("zero","persistence_unavailable")][InlineData("cycle","persistence_unavailable")][InlineData("trashed",null)]
    public async Task GivenChangedRequiredFormerParent_WhenRestoring_ThenPreserveIntegrityOrOmitHiddenParent(string damage,string? error)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var a=await AssociationSetup.Items(fixture,s);
        await using(var db=fixture.CreateContext()){await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));}
        var root=await TrashRestoreSetup.TrashAsync(fixture,s,"record",a.Record.PublicId,1,a.Record.Id,a.Record.EditedAt);
        await using(var db=fixture.CreateContext()){
            if(damage is "max" or "zero")await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.Revision,damage=="max"?ProtocolBinary.MaxInteger:0));
            if(damage=="cycle")await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,a.Folder.Id));
            if(damage=="trashed")await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.DeletedAt,DateTimeOffset.UtcNow));}
        var before=await TrashRestoreSetup.Snapshot(fixture,s);var r=await Restore(s,root);Assert.Equal(error,r.Error);
        if(error is not null)Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));else Assert.Contains(r.Data!.Omissions,x=>x.AssociationKind=="parent"&&x.Count==1);
    }
    [FunctionalFact]
    public async Task GivenReciprocalProfileForeignCollectionLinks_WhenRestoringBoth_ThenShareLocksAvoidDeadlockAndRetainGrants()
    {
        using var ka=new ProtectionFixture();using var kb=new ProtectionFixture();using var sa=new ProtectionFixture();using var sb=new ProtectionFixture();
        var a=await ProfileSetup.Create(fixture,ka);var b=await ProfileSetup.Create(fixture,kb);var ia=await AssociationSetup.Items(fixture,a);var ib=await AssociationSetup.Items(fixture,b);var pa=await Profile(a,ka,sa);var pb=await Profile(b,kb,sb);
        await AssociationSetup.Grant(fixture,a,ka,b,kb,ia.Collection);await AssociationSetup.Grant(fixture,b,kb,a,ka,ib.Collection);
        await using(var db=fixture.CreateContext()){foreach(var c in new[]{ia.Collection,ib.Collection})foreach(var p in new[]{pa,pb})db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=c.Id});await db.SaveChangesAsync();}
        var ra=await TrashRestoreSetup.TrashAsync(fixture,a,"profile",pa.PublicId,pa.Revision,pa.Id,pa.EditedAt);var rb=await TrashRestoreSetup.TrashAsync(fixture,b,"profile",pb.PublicId,pb.Revision,pb.Id,pb.EditedAt);
        var left=new CollectionBarrier();var right=new CollectionBarrier();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first=new TrashRestoreStore(new ProfileSetup.Factory(fixture,left)).RestoreAsync(new(a.Actor,a.Verifier,ra.OperationId,ra.Revision),ct.Token);
        var second=new TrashRestoreStore(new ProfileSetup.Factory(fixture,right)).RestoreAsync(new(b.Actor,b.Verifier,rb.OperationId,rb.Revision),ct.Token);
        try{await Task.WhenAll(left.Ready.Task,right.Ready.Task).WaitAsync(TimeSpan.FromSeconds(10));Assert.False(first.IsCompleted);Assert.False(second.IsCompleted);}
        finally{left.Release.TrySetResult();right.Release.TrySetResult();}
        Assert.All(await Task.WhenAll(first,second),r=>Assert.Null(r.Error));
        await using var check=fixture.CreateContext();Assert.Equal(4,await check.ProfileCollections.CountAsync(x=>(x.ProfileId==pa.Id||x.ProfileId==pb.Id)&&(x.CollectionId==ia.Collection.Id||x.CollectionId==ib.Collection.Id)));
    }
    private sealed class CollectionBarrier:DbCommandInterceptor
    {
        internal TaskCompletionSource Ready{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);internal TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {if(command.CommandText.Contains("FROM cerberus.collection ")&&command.CommandText.Contains("FOR ")){Assert.Contains("FOR SHARE",command.CommandText);Ready.TrySetResult();await Release.Task.WaitAsync(ct);}return result;}
    }
}
