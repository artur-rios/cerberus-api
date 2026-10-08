using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Tests;
[Collection("PostgreSQL")]
public class ProfileAssociationSchemaTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenTypedEqualGuidsAndTwoProfiles_WhenLinking_ThenPermitManyViewsWithoutDuplicatingContent()
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var p1=await AssociationSetup.Profile(fixture,s,owner);var p2=await AssociationSetup.Profile(fixture,s,owner);var items=await AssociationSetup.Items(fixture,s,Guid.NewGuid());await using var db=fixture.CreateContext();foreach(var p in new[]{p1,p2}){db.ProfileRecords.Add(new(){AccountId=s.InternalId,ProfileId=p,RecordId=items.Record.Id});db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=p,FolderId=items.Folder.Id});db.ProfileCollections.Add(new(){ProfileId=p,CollectionId=items.Collection.Id});}await db.SaveChangesAsync();Assert.Equal(2,await db.ProfileRecords.CountAsync(x=>x.RecordId==items.Record.Id));Assert.Equal(2,await db.ProfileFolders.CountAsync(x=>x.FolderId==items.Folder.Id));Assert.Equal(2,await db.ProfileCollections.CountAsync(x=>x.CollectionId==items.Collection.Id));Assert.Equal(items.Record.PublicId,items.Folder.PublicId);Assert.Equal(items.Record.PublicId,items.Collection.PublicId);Assert.True(items.Record.ServerSequence>0);Assert.NotEqual(items.Record.ServerSequence,items.Folder.ServerSequence);
    }
    [FunctionalTheory][InlineData("record")][InlineData("folder")]
    public async Task GivenDirectItemOwnedByAnotherAccount_WhenLinking_ThenDatabaseRejectsOwnershipEscape(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var foreign=await ProfileSetup.Create(fixture,owner);var p=await AssociationSetup.Profile(fixture,s,owner);var items=await AssociationSetup.Items(fixture,foreign);await using var db=fixture.CreateContext();if(kind=="record")db.ProfileRecords.Add(new(){AccountId=s.InternalId,ProfileId=p,RecordId=items.Record.Id});else db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=p,FolderId=items.Folder.Id});await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());
    }
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("grant")]
    public async Task GivenExistingMembershipOrCurrentGrant_WhenDuplicating_ThenDatabaseRejectsDuplicate(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,owner);var p=await AssociationSetup.Profile(fixture,s,owner);var items=await AssociationSetup.Items(fixture,s);await using var db=fixture.CreateContext();void Add(){if(kind=="record")db.ProfileRecords.Add(new(){AccountId=s.InternalId,ProfileId=p,RecordId=items.Record.Id});else if(kind=="folder")db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=p,FolderId=items.Folder.Id});else if(kind=="collection")db.ProfileCollections.Add(new(){ProfileId=p,CollectionId=items.Collection.Id});else db.CollectionGrants.Add(new(){PublicId=Guid.NewGuid(),CollectionId=items.Collection.Id,RecipientAccountId=recipient.InternalId,RecipientKeyEnvelope=[1]});}Add();await db.SaveChangesAsync();db.ChangeTracker.Clear();Add();await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());
    }
    [FunctionalTheory][InlineData("record")][InlineData("folder")]
    public async Task GivenParentFolderOwnedByAnotherAccount_WhenSavingChild_ThenDatabaseRejectsCrossOwnerParent(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var foreign=await ProfileSetup.Create(fixture,owner);var items=await AssociationSetup.Items(fixture,s);var other=await AssociationSetup.Items(fixture,foreign);await using var db=fixture.CreateContext();if(kind=="record")await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.record SET folder_id={other.Folder.Id} WHERE id={items.Record.Id}"));else await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.folder SET parent_folder_id={other.Folder.Id} WHERE id={items.Folder.Id}"));
    }
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")]
    public async Task GivenExistingPublicResourceId_WhenSavingSameKind_ThenDatabaseRejectsDuplicate(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var items=await AssociationSetup.Items(fixture,s);await using var db=fixture.CreateContext();if(kind=="record")db.Records.Add(new(){AccountId=s.InternalId,PublicId=items.Record.PublicId,Envelope=[1],EditedAt=DateTimeOffset.UtcNow});else if(kind=="folder")db.Folders.Add(new(){AccountId=s.InternalId,PublicId=items.Folder.PublicId,Envelope=[1],EditedAt=DateTimeOffset.UtcNow});else db.Collections.Add(new(){AccountId=s.InternalId,PublicId=items.Collection.PublicId,Envelope=[1],EditedAt=DateTimeOffset.UtcNow});await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());
    }
}
internal static class AssociationSetup
{
    internal sealed record ResourceSet(VaultRecord Record,VaultFolder Folder,VaultCollection Collection);
    internal static async Task<long> Profile(PostgresFixture fixture,ProfileSetup.State s,ProtectionFixture owner)
    {using var scoped=new ProtectionFixture();var i=ProfileSetup.Input(s,owner,scoped);Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,i),default)).Error);await using var db=fixture.CreateContext();return await db.Profiles.Where(x=>x.PublicId==i.ProfileId).Select(x=>x.Id).SingleAsync();}
    internal static async Task<ResourceSet> Items(PostgresFixture fixture,ProfileSetup.State s,Guid? sameId=null)
    {var envelope=JsonSerializer.SerializeToUtf8Bytes(ProfileSetup.Envelope(),ProtectionFixture.Json);var now=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());var r=new VaultRecord{AccountId=s.InternalId,PublicId=sameId??Guid.NewGuid(),Envelope=envelope,EditedAt=now};var f=new VaultFolder{AccountId=s.InternalId,PublicId=sameId??Guid.NewGuid(),Envelope=envelope,EditedAt=now};var c=new VaultCollection{AccountId=s.InternalId,PublicId=sameId??Guid.NewGuid(),Envelope=envelope,EditedAt=now};await using var db=fixture.CreateContext();db.Records.Add(r);db.Folders.Add(f);db.Collections.Add(c);await db.SaveChangesAsync();return new(r,f,c);}
}
