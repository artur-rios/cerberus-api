using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public sealed partial class RecordPermanentDeleteStoreTests
{
    [FunctionalTheory]
    [InlineData("unknown")][InlineData("missing")][InlineData("duplicate")][InlineData("stringArray")]
    [InlineData("zero")][InlineData("case")][InlineData("noncanonical")][InlineData("duplicateIds")]
    public async Task GivenMalformedRequiredHistoricalAssociations_WhenDeletingSelectedTrash_Then503WithoutTerminalIntent(string kind)
    {
        using var w=await PermanentDeleteSetup.Create(fixture,"direct",true);
        await using var db=fixture.CreateContext();
        var selected=await db.Profiles.Where(x=>x.Id==w.Selection).Select(x=>x.PublicId).SingleAsync();
        var id=selected.ToString("D");
        var json=kind switch {
            "unknown"=>$"{{\"folderId\":null,\"profileIds\":[\"{id}\"],\"collectionIds\":[],\"extra\":true}}",
            "missing"=>$"{{\"profileIds\":[\"{id}\"],\"collectionIds\":[]}}",
            "duplicate"=>$"{{\"folderId\":null,\"profileIds\":[\"{id}\"],\"profileIds\":[],\"collectionIds\":[]}}",
            "stringArray"=>$"{{\"folderId\":null,\"profileIds\":\"{id}\",\"collectionIds\":[]}}",
            "zero"=>"{\"folderId\":null,\"profileIds\":[\"00000000-0000-0000-0000-000000000000\"],\"collectionIds\":[]}",
            "case"=>$"{{\"FolderId\":null,\"profileIds\":[\"{id}\"],\"collectionIds\":[]}}",
            "noncanonical"=>$"{{\"folderId\":null,\"profileIds\":[\"{selected:N}\"],\"collectionIds\":[]}}",
            _=>$"{{\"folderId\":null,\"profileIds\":[\"{id}\",\"{id}\"],\"collectionIds\":[]}}" };
        await db.TrashEntries.Where(x=>x.ResourceKind=="record" && x.ResourceId==w.Record.PublicId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.AssociationSnapshot,System.Text.Encoding.UTF8.GetBytes(json)));
        var before=await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId);
        Assert.Equal("persistence_unavailable",(await Store().DeleteAsync(w.Request(),default)).Error);
        Assert.Equal(before,await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId));
        Assert.Empty(Directory.EnumerateFiles(directory,"*.json"));
    }

    [FunctionalTheory]
    [InlineData("direct",false)][InlineData("direct",true)][InlineData("collection",false)][InlineData("collection",true)]
    public async Task GivenValidHistoricalDirectRouteAndUnneededHiddenOrCyclicFolder_WhenDeleting_ThenUseCurrentDirectAuthority(string route,bool cycle)
    {
        using var w=await PermanentDeleteSetup.Create(fixture,route,true);
        await using var db=fixture.CreateContext();
        var entry=await db.TrashEntries.SingleAsync(x=>x.ResourceKind=="record" && x.ResourceId==w.Record.PublicId);
        var snapshot=JsonSerializer.Deserialize<RecordAssociationSnapshot>(entry.AssociationSnapshot,ProtectionFixture.Json)!;
        entry.AssociationSnapshot=JsonSerializer.SerializeToUtf8Bytes(snapshot with {FolderId=w.Items.Folder.PublicId},ProtectionFixture.Json);
        await db.SaveChangesAsync();
        if(cycle)await db.Folders.Where(x=>x.Id==w.Items.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ParentFolderId,w.Items.Folder.Id));
        else await db.Folders.Where(x=>x.Id==w.Items.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));
        Assert.Null((await Store().DeleteAsync(w.Request(),default)).Error);
        Assert.False(await db.Records.AnyAsync(x=>x.Id==w.Record.Id));
    }

    [FunctionalTheory]
    [InlineData(CollectionGrantAccess.ReadOnly)][InlineData(CollectionGrantAccess.ReadWrite)]
    public async Task GivenForeignRecordInTrashWithFormerNativeAccess_WhenDeleting_Then404AndNoIntent(CollectionGrantAccess access)
    {
        using var w=await PermanentDeleteSetup.Create(fixture,"collection",false);
        using var client=new ProtectionFixture();var recipient=await ProfileSetup.Create(fixture,client);
        await AssociationSetup.Grant(fixture,w.State,w.Owner,recipient,client,w.Items.Collection,access);
        Assert.Null((await new RecordTrashStore(fixture).TrashAsync(new(w.State.Actor,w.State.Verifier,w.Record.PublicId,1),default)).Error);
        var before=await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId);
        Assert.Equal("not_found",(await Store().DeleteAsync(new(recipient.Actor,recipient.Verifier,w.Record.PublicId,2),default)).Error);
        Assert.Equal(before,await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId));
    }

    [FunctionalTheory]
    [InlineData("profile")][InlineData("collection")][InlineData("folder")]
    public async Task GivenTrashAlreadyAdvancedDirectParents_WhenPermanentlyDeleting_ThenDoNotAdvanceThemAgain(string parent)
    {
        using var w=await PermanentDeleteSetup.Create(fixture,parent=="profile"?"direct":parent,true);
        var before=await PermanentDeleteSetup.Parent(fixture,w,parent);
        Assert.Null((await Store().DeleteAsync(w.Request(),default)).Error);
        Assert.Equal(before,await PermanentDeleteSetup.Parent(fixture,w,parent));
    }

    [FunctionalFact]
    public async Task GivenSoleRootRecordTrashOperation_WhenPermanentlyDeleting_ThenRemoveEmptyOperationAndTrashWorkAndCompletePurgeWork()
    {
        using var w=await PermanentDeleteSetup.Create(fixture,"wide",true);
        await using var db=fixture.CreateContext();
        var operation=await db.TrashOperations.SingleAsync(x=>x.RootResourceKind=="record" && x.RootResourceId==w.Record.PublicId);
        Assert.Null((await Store().DeleteAsync(w.Request(),default)).Error);
        Assert.False(await db.TrashOperations.AnyAsync(x=>x.Id==operation.Id));
        Assert.False(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="trash/"+operation.PublicId));
        Assert.NotNull((await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="record-purge/"+w.Record.PublicId)).CompletedAt);
    }

    [FunctionalTheory]
    [InlineData("profile")][InlineData("collection")]
    public async Task GivenTrashedTargetHasUnretainedLiveLink_WhenDeletingUnderSelectedProfile_ThenLiveLinkCannotWidenRestrictedHistory(string link)
    {
        using var w=await PermanentDeleteSetup.Create(fixture,"direct",true);
        await using var db=fixture.CreateContext();
        var entry=await db.TrashEntries.SingleAsync(x=>x.ResourceKind=="record" && x.ResourceId==w.Record.PublicId);
        entry.AssociationSnapshot=JsonSerializer.SerializeToUtf8Bytes(new RecordAssociationSnapshot(null,[Guid.NewGuid()],[]),ProtectionFixture.Json);
        if(link=="profile")db.ProfileRecords.Add(new(){AccountId=w.State.InternalId,ProfileId=w.Selection!.Value,RecordId=w.Record.Id});
        else
        {
            db.ProfileCollections.Add(new(){ProfileId=w.Selection!.Value,CollectionId=w.Items.Collection.Id});
            db.CollectionRecords.Add(new(){AccountId=w.State.InternalId,CollectionId=w.Items.Collection.Id,RecordId=w.Record.Id});
        }
        await db.SaveChangesAsync();
        var before=await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId);
        Assert.Equal("not_found",(await Store().DeleteAsync(w.Request(),default)).Error);
        Assert.Equal(before,await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId));
        Assert.Empty(Directory.EnumerateFiles(directory,"*.json"));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenInvalidUnusedClientTimestamp_WhenPermanentlyDeleting_ThenOwnedErasureOrForeign403DoesNotParseIt(bool foreign)
    {
        using var w=await PermanentDeleteSetup.Create(fixture,"wide",false);
        using var client=new ProtectionFixture();
        var request=w.Request();
        if(foreign)
        {
            var recipient=await ProfileSetup.Create(fixture,client);
            await using var associations=fixture.CreateContext();
            associations.CollectionRecords.Add(new(){AccountId=w.State.InternalId,CollectionId=w.Items.Collection.Id,RecordId=w.Record.Id});
            await associations.SaveChangesAsync();
            await AssociationSetup.Grant(fixture,w.State,w.Owner,recipient,client,w.Items.Collection,CollectionGrantAccess.ReadWrite);
            request=new(recipient.Actor,recipient.Verifier,w.Record.PublicId,99);
        }
        await using var db=fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.record SET edited_at='-infinity'::timestamptz WHERE id={w.Record.Id}");
        var before=await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId);
        var result=await Store().DeleteAsync(request,default);
        Assert.Equal(foreign?"vault_access_denied":null,result.Error);
        if(foreign)Assert.Equal(before,await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId));
        else Assert.False(await db.Records.AnyAsync(x=>x.Id==w.Record.Id));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenOverlappingNativeRoutesWithOneMalformedGrant_WhenDeletingForeignTarget_ThenValidateEveryContributingRouteOnly(bool removeBad)
    {
        using var w=await PermanentDeleteSetup.Create(fixture,"wide",false);
        using var client=new ProtectionFixture();var recipient=await ProfileSetup.Create(fixture,client);
        await using var db=fixture.CreateContext();
        var second=new VaultCollection{AccountId=w.State.InternalId,PublicId=Guid.NewGuid(),Envelope=w.Items.Collection.Envelope,EditedAt=w.Items.Collection.EditedAt};
        db.Collections.Add(second);await db.SaveChangesAsync();
        db.CollectionRecords.Add(new(){AccountId=w.State.InternalId,CollectionId=w.Items.Collection.Id,RecordId=w.Record.Id});
        db.CollectionRecords.Add(new(){AccountId=w.State.InternalId,CollectionId=second.Id,RecordId=w.Record.Id});await db.SaveChangesAsync();
        await AssociationSetup.Grant(fixture,w.State,w.Owner,recipient,client,w.Items.Collection,CollectionGrantAccess.ReadWrite);
        var bad=await AssociationSetup.Grant(fixture,w.State,w.Owner,recipient,client,second,CollectionGrantAccess.ReadOnly);
        await db.CollectionGrants.Where(x=>x.Id==bad.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.RecipientKeyEnvelope,"bad"u8.ToArray()));
        if(removeBad)await db.CollectionRecords.Where(x=>x.RecordId==w.Record.Id && x.CollectionId==second.Id).ExecuteDeleteAsync();
        var before=await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId);
        Assert.Equal(removeBad?"vault_access_denied":"persistence_unavailable",(await Store().DeleteAsync(new(recipient.Actor,recipient.Verifier,w.Record.PublicId,99),default)).Error);
        Assert.Equal(before,await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId));
    }

    [FunctionalFact]
    public async Task GivenRotationWaitsOnPermanentIntent_WhenItSuppliesOldCompleteInventory_ThenRevalidateAndRejectWithoutProofConsumption()
    {
        using var w=await PermanentDeleteSetup.Create(fixture,"wide",false);
        var replacements=new[]{new ContentReplacement("account",w.State.AccountId,1,ProfileSetup.Envelope(2)),
            new("record",w.Items.Record.PublicId,1,ProfileSetup.Envelope(2)),new("record",w.Record.PublicId,1,ProfileSetup.Envelope(2)),
            new("folder",w.Items.Folder.PublicId,1,ProfileSetup.Envelope(2)),new("collection",w.Items.Collection.PublicId,1,ProfileSetup.Envelope(2))};
        var change=new ProtectionChange(w.State.AccountId,1,1,"rotate-content",w.Owner.Rewrap(),replacements);
        var raw=JsonSerializer.SerializeToUtf8Bytes(change,ProtectionFixture.Json);
        var challenge=(await new VaultProtectionStore(fixture).ChallengeAsync(w.State.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var deletion=Store(factory:new ProfileSetup.Factory(fixture,new BeforeTarget(()=>{entered.TrySetResult();return release.Task.WaitAsync(ct.Token);}))).DeleteAsync(w.Request(),ct.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var blocked=new PermanentDeleteSetup.LockSignal("account");
        var rotation=new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture,blocked)).ChangeAsync(new(w.State.Actor,w.State.Verifier,challenge.ChallengeId,w.Owner.Sign(challenge),raw,change),ct.Token);
        try{await blocked.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(rotation.IsCompleted);}finally{release.TrySetResult();}
        Assert.Null((await deletion).Error);
        Assert.Equal("validation_failed",(await rotation).Error);
        await using var db=fixture.CreateContext();
        Assert.False((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==challenge.ChallengeId)).Consumed);
        Assert.False(await db.Records.AnyAsync(x=>x.Id==w.Record.Id));
        Assert.Equal(1,(await db.Records.SingleAsync(x=>x.Id==w.Items.Record.Id)).Revision);
    }
}
