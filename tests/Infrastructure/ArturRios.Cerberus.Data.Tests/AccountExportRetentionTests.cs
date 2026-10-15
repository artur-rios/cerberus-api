using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Data.Accounts.Export;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountExportStoreTests
{
    [FunctionalFact]
    public async Task GivenCapturedGrandparentBecomesTerminal_WhenReleasingPendingDescendants_ThenAbortBeforeAnyLaterRecordBytes()
    {
        // Given: every record has an ancestor beyond the immediate parent;32rows cannot deliver all97records.
        var w=await World(96);Guid ancestorId;await using(var db=fixture.CreateContext())
        {
            var parent=new ArturRios.Cerberus.Domain.Resources.VaultFolder{AccountId=w.Owner.InternalId,PublicId=Guid.NewGuid(),Envelope=[7,11,13],EditedAt=w.Folder.EditedAt};db.Folders.Add(parent);await db.SaveChangesAsync();ancestorId=parent.PublicId;
            await db.Folders.Where(x=>x.Id==w.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,parent.Id));await db.Records.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,w.Folder.Id));
        }
        await using var lease=await Open(w);await using var reader=lease.ReadAsync(default).GetAsyncEnumerator();for(var i=0;i<32;i++)Assert.True(await reader.MoveNextAsync());await Marker("folder",ancestorId);var winner=await Snapshot(w);
        // When/Then: pending descendants depend on their captured whole ancestry, even if the ancestor row was delivered.
        await Assert.ThrowsAsync<AccountExportException>(async()=>{while(await reader.MoveNextAsync())Assert.IsNotType<AccountExportRecord>(reader.Current);});Assert.Equal(winner,await Snapshot(w));
    }
    [FunctionalTheory][InlineData("profile")][InlineData("record")][InlineData("folder")][InlineData("collection")]
    public async Task GivenOriginalRetainedOperation_WhenExporting_ThenPreserveWindowAndNormalizeOwnedHistoryWithoutRawSnapshots(string kind)
    {
        // Given: original typed history includes owned references plus an unrelated foreign profile.
        var w=await World();var foreign=await World();var snapshot=History(kind,w,foreign.Profile.PublicId);var op=await Retain(w,kind,snapshot);var before=await Snapshot(w);var foreignBefore=await Snapshot(foreign);
        // When/Then: exactly the original operation metadata survives; private recipient organization does not.
        var result=await Capture(w);var exported=Assert.Single(result.Entries.OfType<AccountExportTrashOperation>());
        Assert.Equal(op.PublicId,exported.OperationId);Assert.Equal(kind,exported.RootKind);Assert.Equal(op.RootResourceId,exported.RootId);Assert.Equal(op.DeletedAt,exported.DeletedAt);Assert.Equal(op.PurgeAt,exported.PurgeAt);Assert.Equal(TimeSpan.FromHours(720),exported.PurgeAt-exported.DeletedAt);
        var edges=result.Entries.OfType<AccountExportRelationship>().Where(x=>x.OperationId==op.PublicId).ToArray();Assert.NotEmpty(edges);Assert.All(edges,x=>Assert.Equal("historical",x.RelationshipKind));Assert.DoesNotContain(edges,x=>x.SourceId==foreign.Profile.PublicId||x.TargetId==foreign.Profile.PublicId);
        Assert.Contains(edges,x=>x.SourceKind=="profile"&&x.SourceId==w.Profile.PublicId&&x.TargetKind==(kind switch{"profile"=>"record",_=>kind}));
        var json=JsonSerializer.Serialize(result.Entries,ProtectionFixture.Json);Assert.DoesNotContain("associationSnapshot",json);Assert.DoesNotContain(foreign.Profile.PublicId.ToString(),json);Assert.DoesNotContain(foreign.Owner.AccountId.ToString(),json);Assert.Equal(before,await Snapshot(w));Assert.Equal(foreignBefore,await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData("profile",false)][InlineData("record",false)][InlineData("folder",false)][InlineData("collection",false)]
    [InlineData("profile",true)][InlineData("record",true)][InlineData("folder",true)][InlineData("collection",true)]
    public async Task GivenExpiredOrPermanentHistory_WhenExporting_ThenOmitItsPayloadKeysOperationAndHistoricalEdges(string kind,bool terminal)
    {
        // Given: hidden history deliberately contains malformed bytes that must not be inspected.
        var w=await World();var op=await Retain(w,kind,History(kind,w));await using(var db=fixture.CreateContext())
        {
            await db.TrashEntries.Where(x=>x.OperationId==op.Id).ExecuteUpdateAsync(x=>x.SetProperty(e=>e.AssociationSnapshot,new byte[]{255,0,129}));
            if(terminal)await Marker(kind,op.RootResourceId);
            else{var deleted=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(-721).ToUnixTimeMilliseconds());await Dates(w,kind,deleted,deleted.AddHours(720));await db.TrashOperations.Where(x=>x.Id==op.Id).ExecuteUpdateAsync(x=>x.SetProperty(o=>o.DeletedAt,deleted).SetProperty(o=>o.PurgeAt,deleted.AddHours(720)));}
        }
        var before=await Snapshot(w);
        // When/Then: invisible corrupt history is excluded before parsing or producing metadata.
        var result=await Capture(w);Assert.DoesNotContain(result.Entries.OfType<AccountExportTrashOperation>(),x=>x.OperationId==op.PublicId);Assert.DoesNotContain(result.Entries.OfType<AccountExportRelationship>(),x=>x.OperationId==op.PublicId);
        Assert.DoesNotContain(result.Entries,e=>EntryIdentity(e)==(kind,op.RootResourceId));if(kind=="folder")Assert.Empty(result.Entries.OfType<AccountExportRecord>());Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalTheory][InlineData("unknown")][InlineData("missing")][InlineData("duplicateKey")][InlineData("wrongType")]
    [InlineData("badUuid")][InlineData("zeroUuid")][InlineData("duplicateId")][InlineData("invalidUtf8")][InlineData("invalidJson")]
    [InlineData("kindMismatch")][InlineData("rootMismatch")][InlineData("windowMismatch")]
    public async Task GivenSelectedCorruptRetainedMetadata_WhenOpeningExport_ThenFailBeforeLeaseWithoutMutatingOwner(string damage)
    {
        // Given: selected real retained record metadata cannot be silently dropped or laundered.
        var w=await World();var op=await Retain(w,"record",History("record",w));await using(var db=fixture.CreateContext())
        {
            var json=damage switch
            {
                "unknown"=>"{\"folderId\":null,\"profileIds\":[],\"collectionIds\":[],\"private\":\"canary\"}",
                "missing"=>"{\"folderId\":null,\"profileIds\":[]}",
                "duplicateKey"=>"{\"folderId\":null,\"profileIds\":[],\"profileIds\":[],\"collectionIds\":[]}",
                "wrongType"=>"{\"folderId\":null,\"profileIds\":{},\"collectionIds\":[]}",
                "badUuid"=>"{\"folderId\":null,\"profileIds\":[\"private-canary\"],\"collectionIds\":[]}",
                "zeroUuid"=>"{\"folderId\":null,\"profileIds\":[\"00000000-0000-0000-0000-000000000000\"],\"collectionIds\":[]}",
                "duplicateId"=>JsonSerializer.Serialize(new RecordAssociationSnapshot(null,[w.Profile.PublicId,w.Profile.PublicId],[]),ProtectionFixture.Json),
                "invalidJson"=>"{invalid-json-private-canary",_=>null
            };
            if(json is not null||damage=="invalidUtf8")await db.TrashEntries.Where(x=>x.OperationId==op.Id).ExecuteUpdateAsync(x=>x.SetProperty(e=>e.AssociationSnapshot,damage=="invalidUtf8"?new byte[]{255,0,129}:Encoding.UTF8.GetBytes(json!)));
            if(damage=="kindMismatch")await db.TrashEntries.Where(x=>x.OperationId==op.Id).ExecuteUpdateAsync(x=>x.SetProperty(e=>e.ResourceKind,"folder"));
            if(damage=="rootMismatch")await db.TrashOperations.Where(x=>x.Id==op.Id).ExecuteUpdateAsync(x=>x.SetProperty(o=>o.RootResourceId,Guid.NewGuid()));
            if(damage=="windowMismatch")await db.TrashOperations.Where(x=>x.Id==op.Id).ExecuteUpdateAsync(x=>x.SetProperty(o=>o.PurgeAt,op.PurgeAt.AddHours(1)));
        }
        var before=await Snapshot(w);
        // When/Then: no partial lease or misleading complete package escapes.
        var result=await new AccountExportStore(fixture).OpenAsync(Request(w),default);Assert.Equal("persistence_unavailable",result.Error);Assert.Null(result.Lease);Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalTheory][InlineData("profile")][InlineData("record")][InlineData("folder")][InlineData("collection")]
    public async Task GivenRetainedNativeWithoutOriginalOperation_WhenOpeningExport_ThenRefuseIncompleteHistory(string kind)
    {
        // Given: original operation/entry metadata was lost while retained bytes remain.
        var w=await World();var op=await Retain(w,kind,History(kind,w));await using(var db=fixture.CreateContext())await db.TrashOperations.Where(x=>x.Id==op.Id).ExecuteDeleteAsync();var before=await Snapshot(w);
        // When/Then: absence cannot become a successful incomplete portable package.
        var result=await new AccountExportStore(fixture).OpenAsync(Request(w),default);Assert.Equal("persistence_unavailable",result.Error);Assert.Null(result.Lease);Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalTheory][InlineData("self")][InlineData("cycle")][InlineData("foreignParent")]
    public async Task GivenSelectedUnusableFolderAncestry_WhenOpeningExport_ThenFailWithoutForeignDisclosure(string corruption)
    {
        // Given: actual stored cyclic or cross-owner parent metadata, with FK bypass only in fixture.
        var w=await World();var other=await World();await using(var db=fixture.CreateContext())
        {
            if(corruption=="self")await db.Folders.Where(x=>x.Id==w.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,w.Folder.Id));
            else if(corruption=="cycle"){var f=new ArturRios.Cerberus.Domain.Resources.VaultFolder{AccountId=w.Owner.InternalId,PublicId=Guid.NewGuid(),Envelope=[1],EditedAt=w.Folder.EditedAt,ParentFolderId=w.Folder.Id};db.Folders.Add(f);await db.SaveChangesAsync();await db.Folders.Where(x=>x.Id==w.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ParentFolderId,f.Id));}
            else{await db.Database.OpenConnectionAsync();await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");try{await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.folder SET parent_folder_id={other.Folder.Id} WHERE id={w.Folder.Id}");}finally{await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin");}}
        }
        var before=await Snapshot(w);var foreign=await Snapshot(other);
        // When/Then: an unusable selected graph cannot be labeled complete.
        var result=await new AccountExportStore(fixture).OpenAsync(Request(w),default);Assert.Equal("persistence_unavailable",result.Error);Assert.Null(result.Lease);Assert.Equal(before,await Snapshot(w));Assert.Equal(foreign,await Snapshot(other));
    }

    [FunctionalFact]
    public async Task GivenForeignCorruptHistoryAndOwnerDenied_WhenExporting_ThenPreserveAuthorityPrecedenceWithoutInspectingPrivateHistory()
    {
        // Given: own selected invalid history and unrelated foreign invalid history.
        var w=await World();var foreign=await World();await Retain(w,"record",[255,0,129]);await Retain(foreign,"record",[255,0,129]);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.Revoked,true));var before=await Snapshot(w);var foreignBefore=await Snapshot(foreign);
        // When/Then: denial precedes any inaccessible metadata parsing/dependency error.
        var result=await new AccountExportStore(fixture).OpenAsync(Request(w),default);Assert.Equal("vault_access_denied",result.Error);Assert.Null(result.Lease);Assert.Equal(before,await Snapshot(w));Assert.Equal(foreignBefore,await Snapshot(foreign));
    }

    private static (string Kind,Guid Id)? EntryIdentity(AccountExportEntry e)=>e switch{AccountExportProfile p=>("profile",p.ResourceId),AccountExportRecord r=>("record",r.ResourceId),AccountExportFolder f=>("folder",f.ResourceId),AccountExportCollection c=>("collection",c.ResourceId),_=>null};
    private static byte[] History(string kind,Graph w,Guid? foreignProfile=null)
    {
        var profiles=foreignProfile is {} id?new[]{w.Profile.PublicId,id}:new[]{w.Profile.PublicId};object value=kind switch
        {"profile"=>new ProfileAssociationSnapshot([w.Record.PublicId],[w.Folder.PublicId],[w.Collection.PublicId]),"record"=>new RecordAssociationSnapshot(w.Folder.PublicId,profiles,[w.Collection.PublicId]),"folder"=>new FolderAssociationSnapshot(null,profiles,[w.Collection.PublicId]),_=>new CollectionAssociationSnapshot(profiles,[w.Record.PublicId],[w.Folder.PublicId])};return JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    }
    private async Task<TrashOperation> Retain(Graph w,string kind,byte[] snapshot)
    {
        var deleted=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds());var id=kind switch{"profile"=>w.Profile.PublicId,"record"=>w.Record.PublicId,"folder"=>w.Folder.PublicId,_=>w.Collection.PublicId};await Dates(w,kind,deleted,deleted.AddHours(720));
        var op=new TrashOperation{AccountId=w.Owner.InternalId,RootResourceKind=kind,RootResourceId=id,DeletedAt=deleted,PurgeAt=deleted.AddHours(720)};await using var db=fixture.CreateContext();db.TrashOperations.Add(op);await db.SaveChangesAsync();db.TrashEntries.Add(new(){OperationId=op.Id,ResourceKind=kind,ResourceId=id,AssociationSnapshot=snapshot});await db.SaveChangesAsync();return op;
    }
    private async Task Dates(Graph w,string kind,DateTimeOffset deleted,DateTimeOffset purge)
    {
        await using var db=fixture.CreateContext();switch(kind)
        {
            case "profile":await db.Profiles.Where(x=>x.Id==w.Profile.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,deleted).SetProperty(p=>p.PurgeAt,purge));break;
            case "record":await db.Records.Where(x=>x.Id==w.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,deleted).SetProperty(r=>r.PurgeAt,purge));break;
            case "folder":await db.Folders.Where(x=>x.Id==w.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.DeletedAt,deleted).SetProperty(f=>f.PurgeAt,purge));break;
            default:await db.Collections.Where(x=>x.Id==w.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.DeletedAt,deleted).SetProperty(c=>c.PurgeAt,purge));break;
        }
    }
}
