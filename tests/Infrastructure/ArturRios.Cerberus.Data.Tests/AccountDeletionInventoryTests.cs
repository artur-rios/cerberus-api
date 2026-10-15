using System.Security.Cryptography;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountDeletionStoreTests
{
    [FunctionalFact]
    public async Task GivenPopulatedSoftwareTrashRecoveryAndSyncMetadata_WhenDeleting_ThenPhysicallyRemoveAllOwnedPayloadAndOperationMetadata()
    {
        var w=await Create();var inventory=await Enrich(w);await using var db=fixture.CreateContext();var foreign=await Foreign(db,w);
        Assert.True(await db.SoftwareGrantRecords.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.True(await db.SyncInventories.AnyAsync(x=>x.StreamId==inventory.Stream));Assert.True(await db.TrashEntries.AnyAsync(x=>x.OperationId==inventory.Trash));
        Assert.Null((await Store().DeleteAsync(Request(w.Owner),default)).Error);await Purged(db,w);await InventoryPurged(db,w,inventory);Assert.Equal(foreign,await Foreign(db,w));
        Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="grant"&&x.ResourceId==inventory.Software));
    }
    [FunctionalFact]
    public async Task GivenSameUuidInForeignTypedKinds_WhenDeletingAndReplayingAccount_ThenNeverEraseForeignFolderOrCollection()
    {
        var w=await Create();await using var db=fixture.CreateContext();
        await db.Folders.Where(x=>x.Id==w.Foreign.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.PublicId,w.Owner.AccountId));
        await db.Records.Where(x=>x.Id==w.Owned.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.PublicId,w.Foreign.Collection.PublicId));
        var foreign=await Foreign(db,w);Assert.Null((await Store().DeleteAsync(Request(w.Owner),default)).Error);
        var store=new ArturRios.Cerberus.Data.Erasure.TerminalErasureStore(fixture);
        await foreach(var entry in Ledger.ReadAsync(default))await store.ReapplyAsync(entry,default);
        Assert.Equal(foreign,await Foreign(db,w));Assert.True(await store.IsErasedAsync("account",w.Owner.AccountId,default));
        Assert.False(await store.IsErasedAsync("folder",w.Owner.AccountId,default));Assert.True(await store.IsErasedAsync("record",w.Foreign.Collection.PublicId,default));
        Assert.False(await store.IsErasedAsync("collection",w.Foreign.Collection.PublicId,default));
    }
    private sealed record Inventory(long Stream,long Trash,Guid TrashId,Guid Software,Guid TerminalRecord);
    private async Task<Inventory> Enrich(World w)
    {
        await using var db=fixture.CreateContext();db.ProfileRecords.Add(new(){AccountId=w.Owner.InternalId,ProfileId=w.Profile,RecordId=w.Owned.Record.Id});
        db.ProfileFolders.Add(new(){AccountId=w.Owner.InternalId,ProfileId=w.Profile,FolderId=w.Owned.Folder.Id});db.ProfileCollections.Add(new(){ProfileId=w.Profile,CollectionId=w.Owned.Collection.Id});
        db.CollectionRecords.Add(new(){AccountId=w.Owner.InternalId,CollectionId=w.Owned.Collection.Id,RecordId=w.Owned.Record.Id});db.CollectionFolders.Add(new(){AccountId=w.Owner.InternalId,CollectionId=w.Owned.Collection.Id,FolderId=w.Owned.Folder.Id});
        var software=new SoftwareGrant{PublicId=Guid.NewGuid(),AccountId=w.Owner.InternalId,HeimdallSoftwarePublicId=Guid.NewGuid(),RecipientKey=[1,2,3]};db.SoftwareGrants.Add(software);
        db.VaultRecoveryOperations.Add(new(){AccountId=w.Owner.InternalId,IdempotencyKey=Guid.NewGuid(),RequestHash=ProtocolBinary.Encode(new byte[32]),ProtectionRevision=1,Generation=1,RevocationGeneration=1});
        var stream=new SyncStream{PublicId=Guid.NewGuid(),AccountId=w.Owner.InternalId,ProfileId=w.Profile,CreatedAt=DateTimeOffset.UtcNow,ExpiresAt=DateTimeOffset.UtcNow.AddDays(1),PageSize=10,VisibilityStamp=new byte[32]};db.SyncStreams.Add(stream);
        var terminal=new VaultRecord{PublicId=Guid.NewGuid(),AccountId=w.Owner.InternalId,Envelope=w.Owned.Record.Envelope,EditedAt=w.Owned.Record.EditedAt};db.Records.Add(terminal);
        var child=new VaultFolder{PublicId=Guid.NewGuid(),AccountId=w.Owner.InternalId,ParentFolderId=w.Owned.Folder.Id,Envelope=w.Owned.Folder.Envelope,EditedAt=w.Owned.Folder.EditedAt};db.Folders.Add(child);
        await db.SaveChangesAsync();db.SoftwareGrantRecords.Add(new(){AccountId=w.Owner.InternalId,GrantId=software.Id,RecordId=w.Owned.Record.Id,RecipientKeyEnvelope=[4,5]});
        db.SoftwareGrantCollections.Add(new(){AccountId=w.Owner.InternalId,GrantId=software.Id,CollectionId=w.Owned.Collection.Id,RecipientKeyEnvelope=[6,7]});
        db.SyncInventories.Add(new(){StreamId=stream.Id,ResourceKind="record",ResourceId=w.Owned.Record.PublicId,Fingerprint=new byte[32]});
        db.SyncChanges.Add(new(){StreamId=stream.Id,WindowVersion=1,ResourceKind="record",ResourceId=w.Owned.Record.PublicId,EventType="upsert"});
        db.SyncOperations.Add(new(){AccountId=w.Owner.InternalId,OperationId=Guid.NewGuid(),RequestFingerprint=new byte[32],ResourceKind="record",ResourceId=w.Owned.Record.PublicId,Action="create",Status="applied",Revision=1,ServerSequence=w.Owned.Record.ServerSequence,EditedAt=w.Owned.Record.EditedAt,CreatedAt=DateTimeOffset.UtcNow});
        db.TerminalErasures.Add(new(){ResourceKind="record",ResourceId=terminal.PublicId,DeletedAt=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())});
        db.RetentionWorkItems.Add(new(){OperationKey="record-purge/"+terminal.PublicId,DueAt=DateTimeOffset.UtcNow.AddDays(1)});await db.SaveChangesAsync();
        var challenge=await new VaultProtectionStore(fixture).ChallengeAsync(w.Owner.Actor,ProtocolBinary.Encode(SHA256.HashData("{}"u8.ToArray())),default);Assert.Null(challenge.Error);
        var trashed=await new RecordTrashStore(fixture).TrashAsync(new(w.Owner.Actor,w.Owner.Verifier,w.Owned.Record.PublicId,1),default);Assert.Null(trashed.Error);
        var op=await db.TrashOperations.AsNoTracking().SingleAsync(x=>x.PublicId==trashed.Data!.TrashOperationId);
        return new(stream.Id,op.Id,op.PublicId,software.PublicId,terminal.PublicId);
    }
    private static async Task InventoryPurged(AppDbContext db,World w,Inventory inventory)
    {
        Assert.False(await db.SoftwareGrants.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.SoftwareGrantRecords.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.SoftwareGrantCollections.AnyAsync(x=>x.AccountId==w.Owner.InternalId));
        Assert.False(await db.VaultUnlockChallenges.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.VaultRecoveryOperations.AnyAsync(x=>x.AccountId==w.Owner.InternalId));
        Assert.False(await db.SyncStreams.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.SyncInventories.AnyAsync(x=>x.StreamId==inventory.Stream));Assert.False(await db.SyncChanges.AnyAsync(x=>x.StreamId==inventory.Stream));Assert.False(await db.SyncOperations.AnyAsync(x=>x.AccountId==w.Owner.InternalId));
        Assert.False(await db.ProfileRecords.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.ProfileFolders.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.ProfileCollections.AnyAsync(x=>x.ProfileId==w.Profile));
        Assert.False(await db.CollectionRecords.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.CollectionFolders.AnyAsync(x=>x.AccountId==w.Owner.InternalId));
        Assert.False(await db.TrashOperations.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.TrashEntries.AnyAsync(x=>x.OperationId==inventory.Trash));
        Assert.False(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="trash/"+inventory.TrashId||x.OperationKey=="record-purge/"+inventory.TerminalRecord||x.OperationKey=="account-purge/"+w.Owner.AccountId));
    }
}
