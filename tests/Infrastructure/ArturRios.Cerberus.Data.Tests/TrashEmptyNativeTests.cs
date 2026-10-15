using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Tests;

public partial class TrashEmptyStoreTests
{
    [FunctionalFact]
    public async Task GivenMixedNativeCascadePriorTrashAndForeignSharing_WhenEmptying_ThenOnlyAdmittedOwnedTrashAndItsLinksDisappear()
    {
        using var keys=new ProtectionFixture();using var remote=new ProtectionFixture();var w=await TrashEmptyNativeSetup.Create(fixture,keys,remote);var roots=await TrashEmptyNativeSetup.Trash(fixture,w);var before=await TrashEmptyNativeSetup.Survivors(fixture,w);
        var r=await Store().EmptyAsync(Request(w.Owner),default);Assert.Null(r.Error);Assert.Equal(7,r.Data!.TotalPurged);Assert.Equal(new long[]{3,2,1,1},new[]{"record","folder","collection","profile"}.Select(k=>r.Data.Counts.Single(x=>x.Kind==k).Count));
        Assert.Equal(before,await TrashEmptyNativeSetup.Survivors(fixture,w));foreach(var root in roots)await TrashRestoreSetup.NoOperation(fixture,root.OperationId);
        await TrashEmptyNativeSetup.Gone(fixture,w);Assert.Equal(7,Directory.GetFiles(directory,"*.json").Length);
        var shared=await new CollectionReadStore(fixture).ReadAsync(new(w.Owner.Actor,w.Owner.Verifier,w.Remote.Collection.PublicId),default);Assert.Null(shared.Error);
        await using var db=fixture.CreateContext();Assert.False(await db.CollectionGrants.AnyAsync(x=>x.Id==w.Outgoing.Id));Assert.True(await db.CollectionGrants.AnyAsync(x=>x.Id==w.Incoming.Id&&x.State==CollectionGrantState.Active));
        var old=await db.VaultAccessSessions.AsNoTracking().SingleAsync(x=>x.HandleVerifier==w.SelectedVerifier);Assert.Equal(w.Profile,old.ProfileId);Assert.True(old.Revoked);
    }
    [FunctionalFact]
    public async Task GivenOwnedRecordUuidEqualsForeignCollection_WhenEmptying_ThenTypedLedgerCannotEraseForeignCipherOrGrant()
    {
        using var keys=new ProtectionFixture();using var remoteKeys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var other=await ProfileSetup.Create(fixture,remoteKeys);var remote=await AssociationSetup.Items(fixture,other);var id=remote.Collection.PublicId;
        var created=await new RecordCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,new(id,ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch,[],null)),default);Assert.Null(created.Error);
        var deleted=await new RecordTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,id,1),default);Assert.Null(deleted.Error);
        var before=await TrashRestoreSetup.Resource(fixture,new("collection",id,0,Guid.Empty,0,0,default,default,default));var foreign=await TrashRestoreSetup.Snapshot(fixture,other);
        var result=await Store().EmptyAsync(Request(s),default);Assert.Null(result.Error);Assert.Equal(1,result.Data!.TotalPurged);Assert.Equal(foreign,await TrashRestoreSetup.Snapshot(fixture,other));Assert.Equal(before.GetRawText(),(await TrashRestoreSetup.Resource(fixture,new("collection",id,0,Guid.Empty,0,0,default,default,default))).GetRawText());
        await using var db=fixture.CreateContext();Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="record"&&x.ResourceId==id));Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="collection"&&x.ResourceId==id));
    }
}

internal static class TrashEmptyNativeSetup
{
    internal sealed record World(ProfileSetup.State Owner,ProfileSetup.State Foreign,AssociationSetup.ResourceSet Owned,AssociationSetup.ResourceSet Active,AssociationSetup.ResourceSet Remote,long Profile,long OtherProfile,long ForeignProfile,Guid ProfileId,VaultFolder Child,VaultRecord Nested,VaultRecord Prior,CollectionGrant Incoming,CollectionGrant Outgoing,string SelectedVerifier);
    internal static async Task<World> Create(PostgresFixture f,ProtectionFixture keys,ProtectionFixture remoteKeys)
    {
        var s=await ProfileSetup.Create(f,keys);var other=await ProfileSetup.Create(f,remoteKeys);var a=await AssociationSetup.Items(f,s);var active=await AssociationSetup.Items(f,s);var remote=await AssociationSetup.Items(f,other);var p=await AssociationSetup.Profile(f,s,keys);var p2=await AssociationSetup.Profile(f,s,keys);var fp=await AssociationSetup.Profile(f,other,remoteKeys);
        var incoming=await AssociationSetup.Grant(f,other,remoteKeys,s,keys,remote.Collection);var outgoing=await AssociationSetup.Grant(f,s,keys,other,remoteKeys,a.Collection);
        var child=new VaultFolder{AccountId=s.InternalId,PublicId=Guid.NewGuid(),Envelope=a.Folder.Envelope,EditedAt=a.Folder.EditedAt,ParentFolderId=a.Folder.Id};var prior=new VaultRecord{AccountId=s.InternalId,PublicId=Guid.NewGuid(),Envelope=a.Record.Envelope,EditedAt=a.Record.EditedAt,FolderId=a.Folder.Id};
        var verifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));Guid profileId;
        await using(var db=f.CreateContext()){
            db.Folders.Add(child);db.Records.Add(prior);await db.SaveChangesAsync();
            await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));
            db.ProfileRecords.AddRange(new(){AccountId=s.InternalId,ProfileId=p,RecordId=a.Record.Id},new(){AccountId=s.InternalId,ProfileId=p,RecordId=active.Record.Id});db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=p,FolderId=a.Folder.Id});
            db.ProfileCollections.AddRange(new(){ProfileId=p,CollectionId=a.Collection.Id},new(){ProfileId=p,CollectionId=remote.Collection.Id},new(){ProfileId=p2,CollectionId=remote.Collection.Id},new(){ProfileId=fp,CollectionId=a.Collection.Id},new(){ProfileId=fp,CollectionId=remote.Collection.Id});
            db.CollectionRecords.AddRange(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,RecordId=a.Record.Id},new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,RecordId=active.Record.Id});db.CollectionFolders.AddRange(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,FolderId=a.Folder.Id},new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,FolderId=active.Folder.Id});
            db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,ProfileId=p,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});
            db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,ProfileId=p2,HandleVerifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();profileId=await db.Profiles.Where(x=>x.Id==p).Select(x=>x.PublicId).SingleAsync();}
        var nested=new VaultRecord{AccountId=s.InternalId,PublicId=Guid.NewGuid(),Envelope=a.Record.Envelope,EditedAt=a.Record.EditedAt,FolderId=child.Id};await using(var db=f.CreateContext()){db.Records.Add(nested);await db.SaveChangesAsync();}
        await TrashRestoreSetup.TrashAsync(f,other,"record",remote.Record.PublicId,1,remote.Record.Id,remote.Record.EditedAt);
        return new(s,other,a,active,remote,p,p2,fp,profileId,child,nested,prior,incoming,outgoing,verifier);
    }
    internal static async Task<TrashRestoreSetup.Root[]> Trash(PostgresFixture f,World w)
    {
        var prior=await TrashRestoreSetup.TrashAsync(f,w.Owner,"record",w.Prior.PublicId,1,w.Prior.Id,w.Prior.EditedAt);
        await using var db=f.CreateContext();var p=await db.Profiles.AsNoTracking().SingleAsync(x=>x.Id==w.Profile);
        var profile=await TrashRestoreSetup.TrashAsync(f,w.Owner,"profile",p.PublicId,p.Revision,p.Id,p.EditedAt);
        var c=await db.Collections.AsNoTracking().SingleAsync(x=>x.Id==w.Owned.Collection.Id);var collection=await TrashRestoreSetup.TrashAsync(f,w.Owner,"collection",c.PublicId,c.Revision,c.Id,c.EditedAt);
        var folder=await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==w.Owned.Folder.Id);var root=await TrashRestoreSetup.TrashAsync(f,w.Owner,"folder",folder.PublicId,folder.Revision,folder.Id,folder.EditedAt);return [prior,profile,collection,root];
    }
    internal static async Task<string> Survivors(PostgresFixture f,World w)
    {
        await using var db=f.CreateContext();var other=await db.Profiles.AsNoTracking().SingleAsync(x=>x.Id==w.OtherProfile);var foreignProfile=await db.Profiles.AsNoTracking().SingleAsync(x=>x.Id==w.ForeignProfile);
        var rows=new[]{("record",w.Active.Record.PublicId),("folder",w.Active.Folder.PublicId),("collection",w.Active.Collection.PublicId),("profile",other.PublicId),("profile",foreignProfile.PublicId)};var json=new List<string>();foreach(var(kind,id)in rows)json.Add((await TrashRestoreSetup.Resource(f,new(kind,id,0,Guid.Empty,0,0,default,default,default))).GetRawText());
        return JsonSerializer.Serialize(new{Rows=json,Foreign=await TrashRestoreSetup.Snapshot(f,w.Foreign),Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==w.Owner.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleAsync(x=>x.AccountId==w.Owner.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==w.Owner.InternalId&&x.ProfileId!=w.Profile).OrderBy(x=>x.Id).ToArrayAsync(),Incoming=await db.CollectionGrants.AsNoTracking().SingleAsync(x=>x.Id==w.Incoming.Id),OtherProfileLink=await db.ProfileCollections.AsNoTracking().Where(x=>x.ProfileId==w.OtherProfile).OrderBy(x=>x.CollectionId).ToArrayAsync()});
    }
    internal static async Task Gone(PostgresFixture f,World w)
    {
        await using var db=f.CreateContext();var records=new[]{w.Owned.Record.Id,w.Nested.Id,w.Prior.Id};var folders=new[]{w.Owned.Folder.Id,w.Child.Id};Assert.False(await db.Records.AnyAsync(x=>records.Contains(x.Id)));Assert.False(await db.Folders.AnyAsync(x=>folders.Contains(x.Id)));Assert.False(await db.Collections.AnyAsync(x=>x.Id==w.Owned.Collection.Id));Assert.False(await db.Profiles.AnyAsync(x=>x.Id==w.Profile));
    }
}
