using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Data.Accounts.Export;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountExportStoreTests
{
    [FunctionalFact]
    public async Task GivenOwnedCurrentAssociations_WhenExporting_ThenNormalizeOnlyPublicTypedRelationships()
    {
        // Given: real many-view memberships and a nested folder, without duplicating payloads.
        var w=await World();await Links(w);await using(var db=fixture.CreateContext())
        {
            var parent=new VaultFolder{AccountId=w.Owner.InternalId,PublicId=Guid.NewGuid(),Envelope=[3,5,7],EditedAt=w.Folder.EditedAt};db.Folders.Add(parent);await db.SaveChangesAsync();await db.Folders.Where(x=>x.Id==w.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,parent.Id));
        }
        var before=await Snapshot(w);
        // When/Then: canonical directions retain all7 distinct current relationships.
        var result=await Capture(w);var edges=result.Entries.OfType<AccountExportRelationship>().ToArray();Assert.Equal(7,edges.Length);
        Assert.All(edges,x=>{Assert.Equal("current",x.RelationshipKind);Assert.Null(x.OperationId);});
        Assert.Contains(edges,x=>x.SourceKind=="profile"&&x.SourceId==w.Profile.PublicId&&x.TargetKind=="record"&&x.TargetId==w.Record.PublicId);
        Assert.Contains(edges,x=>x.SourceKind=="profile"&&x.TargetKind=="folder");Assert.Contains(edges,x=>x.SourceKind=="profile"&&x.TargetKind=="collection");
        Assert.Contains(edges,x=>x.SourceKind=="collection"&&x.TargetKind=="record");Assert.Contains(edges,x=>x.SourceKind=="collection"&&x.TargetKind=="folder");
        Assert.Contains(edges,x=>x.SourceKind=="record"&&x.TargetKind=="folder");Assert.Contains(edges,x=>x.SourceKind=="folder"&&x.TargetKind=="folder");
        Assert.Single(result.Entries.OfType<AccountExportRecord>());Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenOwnOutgoingAndSoftwareGrants_WhenExporting_ThenKeepOriginalGrantCountersAndEveryTargetEnvelope(bool revoked)
    {
        // Given: legacy opaque grant bytes are owner data; target wrappers are independently stored.
        var w=await World();var recipient=await World();var grants=await Grants(w,recipient,revoked);var before=await Snapshot(w);var foreign=await Snapshot(recipient);
        // When/Then: public recipient references and original keys/revisions survive unchanged.
        var result=await Capture(w);var outgoing=Assert.Single(result.Entries.OfType<AccountExportCollectionGrant>());
        Assert.Equal(grants.Collection.PublicId,outgoing.GrantId);Assert.Equal(w.Collection.PublicId,outgoing.CollectionId);Assert.Equal(recipient.Owner.AccountId,outgoing.RecipientAccountId);Assert.Equal(7,outgoing.Revision);Assert.Equal("read-write",outgoing.Access);Assert.Equal(revoked?"revoked":"active",outgoing.State);Assert.Equal(grants.Collection.RecipientKeyEnvelope,outgoing.RecipientKeyEnvelope);
        var software=Assert.Single(result.Entries.OfType<AccountExportSoftwareGrant>());Assert.Equal(grants.Software.PublicId,software.GrantId);Assert.Equal(grants.Software.HeimdallSoftwarePublicId,software.SoftwareId);Assert.Equal(9,software.Revision);Assert.Equal(grants.Software.RecipientKey,software.RecipientKey);Assert.Equal(revoked?"revoked":"active",software.State);
        var targets=result.Entries.OfType<AccountExportSoftwareTarget>().ToArray();Assert.Equal(2,targets.Length);
        Assert.Contains(targets,x=>x.GrantId==software.GrantId&&x.ResourceKind=="record"&&x.ResourceId==w.Record.PublicId&&x.RecipientKeyEnvelope.SequenceEqual(grants.RecordKey));
        Assert.Contains(targets,x=>x.GrantId==software.GrantId&&x.ResourceKind=="collection"&&x.ResourceId==w.Collection.PublicId&&x.RecipientKeyEnvelope.SequenceEqual(grants.CollectionKey));
        Assert.Equal(before,await Snapshot(w));Assert.Equal(foreign,await Snapshot(recipient));
    }

    [FunctionalTheory][InlineData("active",true)][InlineData("revoked",false)][InlineData("badAccess",false)]
    [InlineData("ownerClosing",false)][InlineData("terminalOwner",false)][InlineData("terminalCollection",false)]
    [InlineData("terminalGrant",false)][InlineData("retainedCollection",false)][InlineData("unrelatedRecipient",false)]
    public async Task GivenIncomingSharing_WhenExporting_ThenIncludeOnlyCurrentReferencesAndNeverForeignPayloadOrPrivateProfiles(string state,bool included)
    {
        // Given: a recipient view and an owner's private view refer to the same foreign collection.
        var w=await World();var foreign=await World();var unrelated=await World();var canaries=await ForeignCanaries(foreign);var grant=await Incoming(w,foreign);
        await using(var db=fixture.CreateContext())
        {
            db.ProfileCollections.Add(new(){ProfileId=w.Profile.Id,CollectionId=foreign.Collection.Id});db.ProfileCollections.Add(new(){ProfileId=foreign.Profile.Id,CollectionId=w.Collection.Id});await db.SaveChangesAsync();
            switch(state)
            {
                case "revoked":await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Revoked));break;
                case "badAccess":await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Access,(CollectionGrantAccess)99));break;
                case "ownerClosing":await db.Accounts.Where(x=>x.Id==foreign.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));break;
                case "terminalOwner":await Marker("account",foreign.Owner.AccountId);break;
                case "terminalCollection":await Marker("collection",foreign.Collection.PublicId);break;
                case "terminalGrant":await Marker("grant",grant.PublicId);break;
                case "retainedCollection":await db.Collections.Where(x=>x.Id==foreign.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.DeletedAt,DateTimeOffset.UtcNow.AddHours(-1)).SetProperty(c=>c.PurgeAt,DateTimeOffset.UtcNow.AddDays(29)));break;
                case "unrelatedRecipient":await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientAccountId,unrelated.Owner.InternalId));break;
            }
        }
        var before=await Snapshot(w);var foreignBefore=await Snapshot(foreign);
        // When/Then: references are a privacy whitelist, not a copy of a shared owner's vault.
        var result=await Capture(w);var references=result.Entries.OfType<AccountExportSharedReference>().ToArray();Assert.Equal(included?1:0,references.Length);
        if(included){var reference=references.Single();Assert.Equal(foreign.Collection.PublicId,reference.CollectionId);Assert.Equal(foreign.Owner.AccountId,reference.OwnerAccountId);Assert.Equal(grant.PublicId,reference.GrantId);Assert.Equal(4,reference.GrantRevision);Assert.Equal("read-only",reference.Access);}
        Assert.Equal(included,result.Entries.OfType<AccountExportRelationship>().Any(x=>x.SourceKind=="profile"&&x.SourceId==w.Profile.PublicId&&x.TargetKind=="collection"&&x.TargetId==foreign.Collection.PublicId));
        var json=JsonSerializer.Serialize(result.Entries,ProtectionFixture.Json);foreach(var bytes in canaries)Assert.DoesNotContain(Convert.ToBase64String(bytes),json);
        foreach(var id in new[]{foreign.Profile.PublicId,foreign.Record.PublicId,foreign.Folder.PublicId,unrelated.Owner.AccountId,unrelated.Profile.PublicId})Assert.DoesNotContain(id.ToString(),json);
        Assert.Equal(before,await Snapshot(w));Assert.Equal(foreignBefore,await Snapshot(foreign));
    }

    [FunctionalFact]
    public async Task GivenEqualPublicIdsAcrossNativeKinds_WhenOneKindIsTerminal_ThenKeepOtherTypedPayloadsAndKeys()
    {
        // Given: equality of UUIDs across resource kinds is valid; only record is terminal.
        var w=await World();var recipient=await World();var grants=await Grants(w,recipient);await using(var db=fixture.CreateContext())
        {await db.Folders.Where(x=>x.Id==w.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.PublicId,w.Record.PublicId));await db.Collections.Where(x=>x.Id==w.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.PublicId,w.Record.PublicId));}
        await Marker("record",w.Record.PublicId);var before=await Snapshot(w);
        // When/Then: typed permanent intent removes only record and its software target.
        var result=await Capture(w);Assert.Empty(result.Entries.OfType<AccountExportRecord>());Assert.Equal(w.Record.PublicId,Assert.Single(result.Entries.OfType<AccountExportFolder>()).ResourceId);Assert.Equal(w.Record.PublicId,Assert.Single(result.Entries.OfType<AccountExportCollection>()).ResourceId);
        var target=Assert.Single(result.Entries.OfType<AccountExportSoftwareTarget>());Assert.Equal("collection",target.ResourceKind);Assert.Equal(grants.CollectionKey,target.RecipientKeyEnvelope);Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalTheory][InlineData("profileRecord")][InlineData("profileFolder")][InlineData("collectionRecord")][InlineData("collectionFolder")]
    [InlineData("softwareRecord")][InlineData("softwareCollection")][InlineData("softwareGrant")][InlineData("recordParent")]
    public async Task GivenCorruptCrossOwnerStoredEdges_WhenExporting_ThenNeverFollowForeignTargetsOrChangeWinningGraph(string kind)
    {
        // Given: bypass only fixture FK enforcement to reproduce actual stored corruption.
        var w=await World();var other=await World();var unrelated=await World();await Links(w);var grants=await Grants(w,other);var foreignGrants=await Grants(other,unrelated);var canaries=await ForeignCanaries(other);
        await using(var db=fixture.CreateContext())
        {
            await db.Database.OpenConnectionAsync();await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
            try
            {
                switch(kind)
                {
                    case "profileRecord":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.profile_record SET record_id={other.Record.Id} WHERE profile_id={w.Profile.Id}");break;
                    case "profileFolder":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.profile_folder SET folder_id={other.Folder.Id} WHERE profile_id={w.Profile.Id}");break;
                    case "collectionRecord":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.collection_record SET record_id={other.Record.Id} WHERE collection_id={w.Collection.Id}");break;
                    case "collectionFolder":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.collection_folder SET folder_id={other.Folder.Id} WHERE collection_id={w.Collection.Id}");break;
                    case "softwareRecord":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.software_grant_record SET record_id={other.Record.Id} WHERE grant_id={grants.Software.Id}");break;
                    case "softwareCollection":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.software_grant_collection SET collection_id={other.Collection.Id} WHERE grant_id={grants.Software.Id}");break;
                    case "softwareGrant":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.software_grant_record SET grant_id={foreignGrants.Software.Id} WHERE grant_id={grants.Software.Id}");break;
                    case "recordParent":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.record SET folder_id={other.Folder.Id} WHERE id={w.Record.Id}");break;
                }
            }
            finally{await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin");}
        }
        var before=await Snapshot(w);var foreignBefore=await Snapshot(other);
        // When/Then: no foreign native UUID/key/payload is reachable through a corrupt edge.
        var result=await Capture(w);var json=JsonSerializer.Serialize(result.Entries,ProtectionFixture.Json);
        foreach(var id in new[]{other.Profile.PublicId,other.Record.PublicId,other.Folder.PublicId,other.Collection.PublicId,foreignGrants.Software.PublicId})Assert.DoesNotContain(id.ToString(),json);
        foreach(var bytes in canaries)Assert.DoesNotContain(Convert.ToBase64String(bytes),json);Assert.Equal(before,await Snapshot(w));Assert.Equal(foreignBefore,await Snapshot(other));
    }

    [FunctionalTheory][InlineData("grant")][InlineData("collection")][InlineData("account")]
    public async Task GivenPendingSharedReferenceBecomesTerminal_WhenReadingLaterBatches_ThenStopBeforeItsReferenceLeaves(string kind)
    {
        // Given:97references guarantee a pending target regardless of unspecified entry order.
        var w=await World(96);var foreign=await World();var grants=new List<CollectionGrant>{await Incoming(w,foreign)};await using(var db=fixture.CreateContext())
        {
            var collections=Enumerable.Range(0,96).Select(_=>new VaultCollection{AccountId=foreign.Owner.InternalId,PublicId=Guid.NewGuid(),Envelope=[1,2,3],EditedAt=foreign.Collection.EditedAt}).ToArray();db.Collections.AddRange(collections);await db.SaveChangesAsync();
            var extra=collections.Select(c=>new CollectionGrant{PublicId=Guid.NewGuid(),CollectionId=c.Id,RecipientAccountId=w.Owner.InternalId,RecipientKeyEnvelope=[1,2,3]}).ToArray();db.CollectionGrants.AddRange(extra);
            db.ProfileCollections.Add(new(){ProfileId=w.Profile.Id,CollectionId=foreign.Collection.Id});db.ProfileCollections.AddRange(collections.Select(c=>new ProfileCollection{ProfileId=w.Profile.Id,CollectionId=c.Id}));
            await db.SaveChangesAsync();grants.AddRange(extra);
        }
        await using var lease=await Open(w);await using var reader=lease.ReadAsync(default).GetAsyncEnumerator();var delivered=new HashSet<Guid>();
        for(var i=0;i<32;i++){Assert.True(await reader.MoveNextAsync());if(reader.Current is AccountExportRelationship {SourceKind:"profile",TargetKind:"collection"} edge)delivered.Add(edge.TargetId);}
        await using var lookup=fixture.CreateContext();var pending=await lookup.Collections.Where(c=>c.AccountId==foreign.Owner.InternalId&&!delivered.Contains(c.PublicId)).Select(c=>new{c.Id,c.PublicId}).FirstAsync();var target=grants.Single(g=>g.CollectionId==pending.Id);var collectionId=pending.PublicId;
        await Marker(kind,kind switch{"grant"=>target.PublicId,"collection"=>collectionId,_=>foreign.Owner.AccountId});var winner=await Snapshot(w);
        // When/Then: typed reference fences also cover foreign owner/grant/collection IDs.
        await Assert.ThrowsAsync<AccountExportException>(async()=>{while(await reader.MoveNextAsync()){if(reader.Current is AccountExportSharedReference reference)Assert.NotEqual(target.PublicId,reference.GrantId);if(reader.Current is AccountExportRelationship {SourceKind:"profile",TargetKind:"collection"} edge)Assert.NotEqual(collectionId,edge.TargetId);}});Assert.Equal(winner,await Snapshot(w));
    }

    private async Task Links(Graph w)
    {await using var db=fixture.CreateContext();db.ProfileRecords.Add(new(){AccountId=w.Owner.InternalId,ProfileId=w.Profile.Id,RecordId=w.Record.Id});db.ProfileFolders.Add(new(){AccountId=w.Owner.InternalId,ProfileId=w.Profile.Id,FolderId=w.Folder.Id});db.ProfileCollections.Add(new(){ProfileId=w.Profile.Id,CollectionId=w.Collection.Id});db.CollectionRecords.Add(new(){AccountId=w.Owner.InternalId,CollectionId=w.Collection.Id,RecordId=w.Record.Id});db.CollectionFolders.Add(new(){AccountId=w.Owner.InternalId,CollectionId=w.Collection.Id,FolderId=w.Folder.Id});await db.SaveChangesAsync();}
    private sealed record ExportGrants(CollectionGrant Collection,SoftwareGrant Software,byte[] RecordKey,byte[] CollectionKey);
    private async Task<ExportGrants> Grants(Graph w,Graph recipient,bool revoked=false)
    {
        var c=new CollectionGrant{PublicId=Guid.NewGuid(),CollectionId=w.Collection.Id,RecipientAccountId=recipient.Owner.InternalId,Revision=7,Access=CollectionGrantAccess.ReadWrite,State=revoked?CollectionGrantState.Revoked:CollectionGrantState.Active,RecipientKeyEnvelope=[21,0,129,22]};
        var s=new SoftwareGrant{PublicId=Guid.NewGuid(),AccountId=w.Owner.InternalId,HeimdallSoftwarePublicId=Guid.NewGuid(),Revision=9,State=revoked?SoftwareGrantState.Revoked:SoftwareGrantState.Active,RecipientKey=Encoding.UTF8.GetBytes("opaque-own-public-recipient-key")};byte[] recordKey=[31,0,128,32],collectionKey=[41,0,129,42];
        await using var db=fixture.CreateContext();db.CollectionGrants.Add(c);db.SoftwareGrants.Add(s);await db.SaveChangesAsync();db.SoftwareGrantRecords.Add(new(){AccountId=w.Owner.InternalId,GrantId=s.Id,RecordId=w.Record.Id,RecipientKeyEnvelope=recordKey});db.SoftwareGrantCollections.Add(new(){AccountId=w.Owner.InternalId,GrantId=s.Id,CollectionId=w.Collection.Id,RecipientKeyEnvelope=collectionKey});await db.SaveChangesAsync();return new(c,s,recordKey,collectionKey);
    }
    private async Task<CollectionGrant> Incoming(Graph recipient,Graph owner)
    {var grant=new CollectionGrant{PublicId=Guid.NewGuid(),CollectionId=owner.Collection.Id,RecipientAccountId=recipient.Owner.InternalId,Revision=4,RecipientKeyEnvelope=[201,202,203]};await using var db=fixture.CreateContext();db.CollectionGrants.Add(grant);await db.SaveChangesAsync();return grant;}
    private async Task Marker(string kind,Guid id)
    {await using var db=fixture.CreateContext();db.TerminalErasures.Add(new(){ResourceKind=kind,ResourceId=id,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
    private async Task<byte[][]> ForeignCanaries(Graph w)
    {
        var values=Enumerable.Range(1,10).Select(i=>Encoding.UTF8.GetBytes("private-foreign-uc54-"+i+"-"+Guid.NewGuid().ToString("N"))).ToArray();await using var db=fixture.CreateContext();
        await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.DetailsEnvelope,values[0]));await db.VaultProtections.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Material,values[1]));await db.Profiles.Where(x=>x.Id==w.Profile.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Envelope,values[2]).SetProperty(p=>p.KeyWrappers,values[3]));await db.Records.Where(x=>x.Id==w.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,values[4]));await db.Folders.Where(x=>x.Id==w.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.Envelope,values[5]));await db.Collections.Where(x=>x.Id==w.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,values[6]));
        await db.SoftwareGrants.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKey,values[7]));await db.SoftwareGrantRecords.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(t=>t.RecipientKeyEnvelope,values[8]));await db.SoftwareGrantCollections.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(t=>t.RecipientKeyEnvelope,values[9]));return values;
    }
}
