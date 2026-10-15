using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public partial class TrashRestoreAssociationTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenOriginalRecordLinks_WhenRestoringWide_ThenReattachCurrentOwnedParentsOnce()
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var a=await AssociationSetup.Items(fixture,s);var p=await Profile(s,keys,scoped);
        await Link(s,a,p);var root=await TrashRestoreSetup.TrashAsync(fixture,s,"record",a.Record.PublicId,1,a.Record.Id,a.Record.EditedAt);
        var metadata=await Parents(a,p);var before=await TrashRestoreSetup.Resource(fixture,root);var r=await Restore(s,root);Assert.Null(r.Error);Assert.Empty(r.Data!.Omissions);
        await using var db=fixture.CreateContext();var row=await db.Records.SingleAsync(x=>x.Id==a.Record.Id);Assert.Equal(a.Folder.Id,row.FolderId);
        Assert.True(await db.ProfileRecords.AnyAsync(x=>x.ProfileId==p.Id&&x.RecordId==a.Record.Id));Assert.True(await db.CollectionRecords.AnyAsync(x=>x.CollectionId==a.Collection.Id&&x.RecordId==a.Record.Id));
        var after=await Parents(a,p);Assert.Equal(metadata.Select(x=>x+1),after);Assert.Equal(before.GetProperty("envelope").GetRawText(),(await TrashRestoreSetup.Resource(fixture,root)).GetProperty("envelope").GetRawText());await TrashRestoreSetup.NoOperation(fixture,root.OperationId);
    }
    [FunctionalFact]
    public async Task GivenMissingOriginalLinks_WhenRestoring_ThenOwnedOrphanAndOnlyOmissionCounts()
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var a=await AssociationSetup.Items(fixture,s);var p=await Profile(s,keys,scoped);await Link(s,a,p);
        var root=await TrashRestoreSetup.TrashAsync(fixture,s,"record",a.Record.PublicId,1,a.Record.Id,a.Record.EditedAt);
        await using(var db=fixture.CreateContext()){await db.Profiles.Where(x=>x.Id==p.Id).ExecuteDeleteAsync();await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteDeleteAsync();await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteDeleteAsync();}
        var r=await Restore(s,root);Assert.Null(r.Error);Assert.Equal(new[]{"collection","parent","profile"},r.Data!.Omissions.Select(x=>x.AssociationKind).Order());Assert.All(r.Data.Omissions,x=>{Assert.Equal(root.ResourceId,x.ResourceId);Assert.Equal(1,x.Count);});
        await using var check=fixture.CreateContext();var row=await check.Records.SingleAsync(x=>x.Id==a.Record.Id);Assert.Null(row.DeletedAt);Assert.Null(row.FolderId);Assert.False(await check.ProfileRecords.AnyAsync(x=>x.RecordId==a.Record.Id));Assert.False(await check.CollectionRecords.AnyAsync(x=>x.RecordId==a.Record.Id));
        var wire=JsonSerializer.Serialize(r.Data,ProtectionFixture.Json);Assert.DoesNotContain(p.PublicId.ToString(),wire);Assert.DoesNotContain(a.Folder.PublicId.ToString(),wire);Assert.DoesNotContain(a.Collection.PublicId.ToString(),wire);
    }
    [FunctionalTheory][InlineData("direct")][InlineData("collection")][InlineData("folder")]
    public async Task GivenRetainedSelectedRootAuthority_WhenRestoring_ThenCurrentSelectionRemainsRestricted(string route)
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var a=await AssociationSetup.Items(fixture,s);var p=await Profile(s,keys,scoped);
        await using(var db=fixture.CreateContext()){
            if(route=="direct")db.ProfileRecords.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,RecordId=a.Record.Id});
            if(route=="collection"){db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=a.Collection.Id});db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,RecordId=a.Record.Id});}
            if(route=="folder"){db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,FolderId=a.Folder.Id});await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));}await db.SaveChangesAsync();}
        var root=await TrashRestoreSetup.TrashAsync(fixture,s,"record",a.Record.PublicId,1,a.Record.Id,a.Record.EditedAt);await Select(s,p);
        var r=await Restore(s,root);Assert.Null(r.Error);Assert.Equal(root.ResourceId,Assert.Single(r.Data!.Items).ResourceId);
        var read=await new RecordReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,a.Record.PublicId),default);Assert.Null(read.Error);
        await using var check=fixture.CreateContext();Assert.Equal(p.Id,(await check.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier)).ProfileId);
    }
    [FunctionalFact]
    public async Task GivenRetainedRootWithNowHiddenAssociations_WhenRestoringSelected_ThenOmitOutsideScopeWithoutLeakingIds()
    {
        using var keys=new ProtectionFixture();using var first=new ProtectionFixture();using var second=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var a=await AssociationSetup.Items(fixture,s);var b=await AssociationSetup.Items(fixture,s);
        var p=await Profile(s,keys,first);var q=await Profile(s,keys,second);await Link(s,a,p);await using(var db=fixture.CreateContext()){
            db.ProfileRecords.Add(new(){AccountId=s.InternalId,ProfileId=q.Id,RecordId=a.Record.Id});db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=b.Collection.Id,RecordId=a.Record.Id});
            db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=a.Collection.Id});await db.SaveChangesAsync();}
        var root=await TrashRestoreSetup.TrashAsync(fixture,s,"record",a.Record.PublicId,1,a.Record.Id,a.Record.EditedAt);
        // The original parent loses selection; the root's direct retained P link still admits the operation.
        await using(var db=fixture.CreateContext())await db.ProfileFolders.Where(x=>x.ProfileId==p.Id).ExecuteDeleteAsync();await Select(s,p);
        var r=await Restore(s,root);Assert.Null(r.Error);Assert.Equal(new[]{"collection","parent","profile"},r.Data!.Omissions.Select(x=>x.AssociationKind).Order());
        await using var check=fixture.CreateContext();Assert.Equal(new[]{p.Id},await check.ProfileRecords.Where(x=>x.RecordId==a.Record.Id).Select(x=>x.ProfileId).ToArrayAsync());Assert.Equal(new[]{a.Collection.Id},await check.CollectionRecords.Where(x=>x.RecordId==a.Record.Id).Select(x=>x.CollectionId).ToArrayAsync());Assert.Null((await check.Records.SingleAsync(x=>x.Id==a.Record.Id)).FolderId);
        var wire=JsonSerializer.Serialize(r.Data,ProtectionFixture.Json);Assert.DoesNotContain(q.PublicId.ToString(),wire);Assert.DoesNotContain(b.Collection.PublicId.ToString(),wire);Assert.DoesNotContain(a.Folder.PublicId.ToString(),wire);
    }
    [FunctionalFact]
    public async Task GivenActuallyForeignSelectedCollectionInRetainedEvidence_WhenListingOrRestoring_ThenCannotAuthorizeOwnedRoot()
    {
        using var keys=new ProtectionFixture();using var foreignKey=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var other=await ProfileSetup.Create(fixture,foreignKey);
        var own=await AssociationSetup.Items(fixture,s);var foreign=await AssociationSetup.Items(fixture,other);var p=await Profile(s,keys,scoped);
        await AssociationSetup.Grant(fixture,other,foreignKey,s,keys,foreign.Collection);
        var root=await TrashRestoreSetup.TrashAsync(fixture,s,"record",own.Record.PublicId,1,own.Record.Id,own.Record.EditedAt);
        await using(var db=fixture.CreateContext()){db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=foreign.Collection.Id});await db.SaveChangesAsync();
            await db.TrashEntries.Where(x=>x.ResourceKind=="record"&&x.ResourceId==own.Record.PublicId).ExecuteUpdateAsync(x=>x.SetProperty(e=>e.AssociationSnapshot,JsonSerializer.SerializeToUtf8Bytes(new RecordAssociationSnapshot(null,[],[foreign.Collection.PublicId]),ProtectionFixture.Json)));}
        await Select(s,p);var before=await TrashRestoreSetup.Snapshot(fixture,s);
        var list=await new TrashListStore(fixture).ListAsync(new(s.Actor,s.Verifier,50,0,null),default);Assert.Null(list.Error);Assert.Empty(list.Data!.Items);
        var r=await Restore(s,root);Assert.Equal("not_found",r.Error);Assert.Null(r.Data);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenFolderCascadeAndIndependentPriorTrash_WhenRestoring_ThenOnlyThatOperationAndOriginalTopology(bool missingChild)
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var a=await AssociationSetup.Items(fixture,s);var p=await Profile(s,keys,scoped);
        var child=new VaultFolder{AccountId=s.InternalId,PublicId=Guid.NewGuid(),ParentFolderId=a.Folder.Id,Envelope=a.Folder.Envelope,EditedAt=a.Folder.EditedAt};
        await using(var db=fixture.CreateContext()){db.Folders.Add(child);await db.SaveChangesAsync();await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));}
        var prior=await TrashRestoreSetup.TrashAsync(fixture,s,"record",a.Record.PublicId,1,a.Record.Id,a.Record.EditedAt);
        var record=new VaultRecord{AccountId=s.InternalId,PublicId=Guid.NewGuid(),FolderId=child.Id,Envelope=a.Record.Envelope,EditedAt=a.Record.EditedAt};
        await using(var db=fixture.CreateContext()){db.Records.Add(record);db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,FolderId=a.Folder.Id});db.CollectionFolders.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,FolderId=a.Folder.Id});await db.SaveChangesAsync();}
        await using var read=fixture.CreateContext();var original=await read.Folders.AsNoTracking().SingleAsync(x=>x.Id==a.Folder.Id);
        var root=await TrashRestoreSetup.TrashAsync(fixture,s,"folder",a.Folder.PublicId,original.Revision,a.Folder.Id,a.Folder.EditedAt);
        if(missingChild){await using var db=fixture.CreateContext();await db.Records.Where(x=>x.Id==record.Id).ExecuteDeleteAsync();}
        var priorBefore=await TrashRestoreSetup.Resource(fixture,prior);var r=await Restore(s,root);Assert.Null(r.Error);Assert.Equal(missingChild?2:3,r.Data!.Items.Count);Assert.DoesNotContain(r.Data.Items,x=>x.ResourceId==prior.ResourceId);
        await using var check=fixture.CreateContext();Assert.Null((await check.Folders.SingleAsync(x=>x.Id==a.Folder.Id)).DeletedAt);Assert.Equal(a.Folder.Id,(await check.Folders.SingleAsync(x=>x.Id==child.Id)).ParentFolderId);
        if(!missingChild){var restored=await check.Records.SingleAsync(x=>x.Id==record.Id);Assert.Null(restored.DeletedAt);Assert.Equal(child.Id,restored.FolderId);}else Assert.Contains(r.Data.Omissions,x=>x.AssociationKind=="resource"&&x.Count==1);
        Assert.Equal(priorBefore.GetRawText(),(await TrashRestoreSetup.Resource(fixture,prior)).GetRawText());Assert.True(await check.TrashOperations.AnyAsync(x=>x.PublicId==prior.OperationId));Assert.True(await check.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="trash/"+prior.OperationId));await TrashRestoreSetup.NoOperation(fixture,root.OperationId);
    }
    [FunctionalFact]
    public async Task GivenRestoredCollectionWithSeparatelyDeletedMember_WhenRestoring_ThenRevalidateLinksAndKeepGrantsRevoked()
    {
        using var keys=new ProtectionFixture();using var recipientKeys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var recipient=await ProfileSetup.Create(fixture,recipientKeys);var a=await AssociationSetup.Items(fixture,s);var p=await Profile(s,keys,scoped);
        await using(var db=fixture.CreateContext()){db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=a.Collection.Id});db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,RecordId=a.Record.Id});db.CollectionFolders.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,FolderId=a.Folder.Id});await db.SaveChangesAsync();}
        var grant=await AssociationSetup.Grant(fixture,s,keys,recipient,recipientKeys,a.Collection);
        var root=await TrashRestoreSetup.TrashAsync(fixture,s,"collection",a.Collection.PublicId,1,a.Collection.Id,a.Collection.EditedAt);
        await using var initial=fixture.CreateContext();var row=await initial.Records.AsNoTracking().SingleAsync(x=>x.Id==a.Record.Id);
        var separate=await TrashRestoreSetup.TrashAsync(fixture,s,"record",row.PublicId,row.Revision,row.Id,row.EditedAt);
        var revoked=JsonSerializer.Serialize(await initial.CollectionGrants.AsNoTracking().SingleAsync(x=>x.Id==grant.Id));var r=await Restore(s,root);Assert.Null(r.Error);Assert.Contains(r.Data!.Omissions,x=>x.AssociationKind=="record"&&x.Count==1);
        await using var check=fixture.CreateContext();Assert.True(await check.ProfileCollections.AnyAsync(x=>x.ProfileId==p.Id&&x.CollectionId==a.Collection.Id));Assert.True(await check.CollectionFolders.AnyAsync(x=>x.CollectionId==a.Collection.Id&&x.FolderId==a.Folder.Id));Assert.False(await check.CollectionRecords.AnyAsync(x=>x.CollectionId==a.Collection.Id&&x.RecordId==a.Record.Id));
        Assert.Equal(revoked,JsonSerializer.Serialize(await check.CollectionGrants.AsNoTracking().SingleAsync(x=>x.Id==grant.Id)));Assert.NotNull((await check.Records.SingleAsync(x=>x.Id==a.Record.Id)).DeletedAt);Assert.True(await check.TrashOperations.AnyAsync(x=>x.PublicId==separate.OperationId));
        Assert.Equal("not_found",(await new CollectionReadStore(fixture).ReadAsync(new(recipient.Actor,recipient.Verifier,a.Collection.PublicId),default)).Error);
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenProfileWithForeignCollection_WhenRestoring_ThenOnlyCurrentNativeGrantAndOldSessionsStayRevoked(bool revoke)
    {
        using var keys=new ProtectionFixture();using var foreignKeys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var other=await ProfileSetup.Create(fixture,foreignKeys);var a=await AssociationSetup.Items(fixture,s);var foreign=await AssociationSetup.Items(fixture,other);var p=await Profile(s,keys,scoped);
        var grant=await AssociationSetup.Grant(fixture,other,foreignKeys,s,keys,foreign.Collection);
        var verifier=Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));await using(var db=fixture.CreateContext()){db.ProfileRecords.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,RecordId=a.Record.Id});db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,FolderId=a.Folder.Id});db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=foreign.Collection.Id});db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();}
        var root=await TrashRestoreSetup.TrashAsync(fixture,s,"profile",p.PublicId,1,p.Id,p.EditedAt);
        if(revoke){await using var db=fixture.CreateContext();await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Revoked));}
        var before=await TrashRestoreSetup.Protected(fixture,s);var r=await Restore(s,root);Assert.Null(r.Error);
        await using var check=fixture.CreateContext();Assert.True(await check.ProfileRecords.AnyAsync(x=>x.ProfileId==p.Id&&x.RecordId==a.Record.Id));Assert.True(await check.ProfileFolders.AnyAsync(x=>x.ProfileId==p.Id&&x.FolderId==a.Folder.Id));Assert.Equal(!revoke,await check.ProfileCollections.AnyAsync(x=>x.ProfileId==p.Id&&x.CollectionId==foreign.Collection.Id));
        var old=await check.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==verifier);Assert.True(old.Revoked);Assert.Equal(p.Id,old.ProfileId);Assert.Equal(before,await TrashRestoreSetup.Protected(fixture,s));if(revoke)Assert.Contains(r.Data!.Omissions,x=>x.AssociationKind=="collection"&&x.Count==1);
    }
    private Task<VaultResult<TrashRestoreDetails>> Restore(ProfileSetup.State s,TrashRestoreSetup.Root r)=>new TrashRestoreStore(fixture).RestoreAsync(new(s.Actor,s.Verifier,r.OperationId,r.Revision),default);
    private async Task<Profile> Profile(ProfileSetup.State s,ProtectionFixture keys,ProtectionFixture scoped)
    {var input=ProfileSetup.Input(s,keys,scoped);Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);await using var db=fixture.CreateContext();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==input.ProfileId);}
    private async Task Select(ProfileSetup.State s,Profile p){await using var db=fixture.CreateContext();await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,p.Id));}
    private async Task Link(ProfileSetup.State s,AssociationSetup.ResourceSet a,Profile p)
    {await using var db=fixture.CreateContext();await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));db.ProfileRecords.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,RecordId=a.Record.Id});db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,FolderId=a.Folder.Id});db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,RecordId=a.Record.Id});await db.SaveChangesAsync();}
    private async Task<long[]> Parents(AssociationSetup.ResourceSet a,Profile p){await using var db=fixture.CreateContext();return [await db.Profiles.Where(x=>x.Id==p.Id).Select(x=>x.Revision).SingleAsync(),await db.Collections.Where(x=>x.Id==a.Collection.Id).Select(x=>x.Revision).SingleAsync(),await db.Folders.Where(x=>x.Id==a.Folder.Id).Select(x=>x.Revision).SingleAsync()];}
}
