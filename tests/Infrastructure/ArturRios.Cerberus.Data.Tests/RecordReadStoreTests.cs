using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Xunit.Abstractions;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class RecordReadStoreTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [FunctionalFact]
    public async Task GivenOwnedRecordWithNoRelationships_WhenGetting_ThenReturnExactCiphertextAndNoInventedReferences()
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var record=await Record(s);
        var before=await Snapshot(s);var r=await Read(s,record.PublicId);
        Assert.Null(r.Error);Assert.Equal(record.PublicId,r.Data!.Record.RecordId);Assert.Equal(record.Envelope,r.Data.Record.Envelope);
        Assert.Equal(record.Revision,r.Data.Record.Revision);Assert.Equal(record.ServerSequence,r.Data.Record.ServerSequence);Assert.Equal(record.EditedAt,r.Data.Record.EditedAt);
        Assert.Empty(r.Data.ProfileIds);Assert.Empty(r.Data.CollectionIds);Assert.Null(r.Data.FolderId);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenMultipleDirectProfilesAndCollections_WhenGetting_ThenReturnOnlyCurrentVisibleReferences(bool selected)
    {
        using var owner=new ProtectionFixture();using var key1=new ProtectionFixture();using var key2=new ProtectionFixture();
        var s=await ProfileSetup.Create(fixture,owner);var root=await Folder(s);var child=await Folder(s,root.Id);var record=await Record(s,child.Id);
        var a=await AssociationSetup.Items(fixture,s);var b=await AssociationSetup.Items(fixture,s);var outside=await AssociationSetup.Items(fixture,s);
        await Members(s,a.Collection,[record.Id],[root.Id]);await Members(s,b.Collection,[record.Id],[]);
        var p=await Profile(s,owner,key1,[record.PublicId],[root.PublicId],[a.Collection.PublicId]);var q=await Profile(s,owner,key2,[record.PublicId],collections:[b.Collection.PublicId]);
        var request=selected?s with{Verifier=await Selected(s,p.Id)}:s;var before=await Snapshot(s);var r=await Read(request,record.PublicId);
        Assert.Null(r.Error);Assert.Equal((selected?new[]{p.PublicId}:new[]{p.PublicId,q.PublicId}).Order(),r.Data!.ProfileIds);
        Assert.Equal((selected?new[]{a.Collection.PublicId}:new[]{a.Collection.PublicId,b.Collection.PublicId}).Order(),r.Data.CollectionIds);
        Assert.DoesNotContain(outside.Collection.PublicId,r.Data.CollectionIds);Assert.Equal(child.PublicId,r.Data.FolderId);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("direct",false)][InlineData("profileFolder",true)][InlineData("collectionRecord",false)][InlineData("collectionFolder",true)]
    public async Task GivenSelectedRecordRoute_WhenGetting_ThenDiscloseParentOnlyThroughAnActualFolderRoute(string route,bool parentVisible)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);
        var root=await Folder(s);var child=await Folder(s,root.Id);var record=await Record(s,child.Id);var a=await AssociationSetup.Items(fixture,s);
        await Members(s,a.Collection,route=="collectionRecord"?[record.Id]:[],route=="collectionFolder"?[root.Id]:[]);
        var p=await Profile(s,owner,key,route=="direct"?[record.PublicId]:[],route=="profileFolder"?[root.PublicId]:[],route.StartsWith("collection")?[a.Collection.PublicId]:[]);
        var r=await Read(s with{Verifier=await Selected(s,p.Id)},record.PublicId);Assert.Null(r.Error);Assert.Equal(parentVisible?child.PublicId:null,r.Data!.FolderId);
        Assert.Equal(route=="direct"?new[]{p.PublicId}:[],r.Data.ProfileIds);Assert.Equal(route.StartsWith("collection")?new[]{a.Collection.PublicId}:[],r.Data.CollectionIds);
    }
    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly,"direct",false)][InlineData(CollectionGrantAccess.ReadOnly,"folder",false)][InlineData(CollectionGrantAccess.ReadOnly,"both",true)]
    [InlineData(CollectionGrantAccess.ReadWrite,"direct",true)][InlineData(CollectionGrantAccess.ReadWrite,"folder",true)][InlineData(CollectionGrantAccess.ReadWrite,"both",false)]
    public async Task GivenNativeSharedRecordWithPrivateOwnerOrganization_WhenGetting_ThenReturnOnlyIncludedRecipientReferences(CollectionGrantAccess access,string route,bool selected)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var ownerKey=new ProtectionFixture();using var recipientKey=new ProtectionFixture();
        var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var a=await AssociationSetup.Items(fixture,foreign);var b=await AssociationSetup.Items(fixture,foreign);
        var child=await Folder(foreign,a.Folder.Id);var record=await Record(foreign,child.Id);
        await Members(foreign,a.Collection,route is "direct" or "both"?[record.Id]:[],route is "folder" or "both"?[a.Folder.Id]:[]);
        await Members(foreign,b.Collection,[record.Id],[]);await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,a.Collection,access);await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,b.Collection,access);
        await Profile(foreign,owner,ownerKey,[record.PublicId],[a.Folder.PublicId],[a.Collection.PublicId]);var p=await Profile(s,recipient,recipientKey,collections:[a.Collection.PublicId]);
        var request=selected?s with{Verifier=await Selected(s,p.Id)}:s;var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);var r=await Read(request,record.PublicId);
        Assert.Null(r.Error);Assert.Empty(r.Data!.ProfileIds);Assert.Equal((selected?new[]{a.Collection.PublicId}:new[]{a.Collection.PublicId,b.Collection.PublicId}).Order(),r.Data.CollectionIds);
        Assert.Equal(route=="direct"?null:child.PublicId,r.Data.FolderId);Assert.Equal(record.Envelope,r.Data.Record.Envelope);Assert.Equal(before,await Snapshot(s));Assert.Equal(ownerBefore,await Snapshot(foreign));
        Assert.Equal("not_found",(await Read(s,a.Record.PublicId)).Error);
    }
    [FunctionalTheory][InlineData("missing")][InlineData("foreign")][InlineData("unselected")][InlineData("unselectedCorruptGrant")]
    public async Task GivenInaccessibleTarget_WhenGetting_Then404WithoutInspectingItsCiphertextOrGrant(string kind)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,recipient);var foreign=await ProfileSetup.Create(fixture,owner);
        var a=await AssociationSetup.Items(fixture,kind=="unselected"?s:foreign);var p=await Profile(s,recipient,key);
        if(kind=="unselectedCorruptGrant"){var grant=await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,a.Collection);await Members(foreign,a.Collection,[a.Record.Id],[]);await using var db=fixture.CreateContext();await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));}
        await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()));
        var r=await Read(kind.StartsWith("unselected")?s with{Verifier=await Selected(s,p.Id)}:s,kind=="missing"?Guid.NewGuid():a.Record.PublicId);Assert.Equal("not_found",r.Error);Assert.Null(r.Data);
    }
    [FunctionalTheory][InlineData(false,false,false)][InlineData(true,false,false)][InlineData(true,true,false)][InlineData(false,true,true)][InlineData(true,false,true)]
    public async Task GivenCyclicTargetAncestry_WhenGetting_Then503OnlyForRelevantFullyActiveContent(bool selected,bool hidden,bool outside)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var root=await Folder(s);var child=await Folder(s,root.Id);var record=await Record(s,child.Id);
        var p=await Profile(s,owner,key,outside?[]:[record.PublicId]);await using(var db=fixture.CreateContext())await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,child.Id).SetProperty(f=>f.DeletedAt,hidden?(DateTimeOffset?)DateTimeOffset.UtcNow:null));
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(10));var r=await Store().ReadAsync(new(s.Actor,selected||outside?await Selected(s,p.Id):s.Verifier,record.PublicId),ct.Token);
        Assert.Equal(hidden||outside?"not_found":"persistence_unavailable",r.Error);Assert.Null(r.Data);
    }
    [FunctionalTheory][InlineData("zero")][InlineData("negative")][InlineData("unsafe")][InlineData("duplicate")]
    public async Task GivenAnotherCorruptRecord_WhenGettingValidTarget_ThenDoNotInspectUnrelatedOrderingOrContent(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);var other=await Record(s);
        await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==other.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.ServerSequence,kind=="zero"?0:kind=="negative"?-1:kind=="unsafe"?ProtocolBinary.MaxInteger+1:target.ServerSequence).SetProperty(r=>r.Envelope,"bad"u8.ToArray()));
        var r=await Read(s,target.PublicId);Assert.Null(r.Error);Assert.Equal(target.PublicId,r.Data!.Record.RecordId);
    }
    [FunctionalTheory][InlineData(0L)][InlineData(-1L)][InlineData(9007199254740992L)]
    public async Task GivenInvalidTargetSequence_WhenGetting_Then503WithoutPayload(long sequence)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Record(s);await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.ServerSequence,sequence));var r=await Read(s,target.PublicId);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);
    }
    [FunctionalTheory][InlineData("actor","authentication_required")][InlineData("target","validation_failed")]
    public async Task GivenInvalidTrustedRequest_WhenGetting_ThenRejectBeforeUnavailableDependency(string kind,string error)
    {
        var dead=new RecordReadStore(new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=unavailable;Username=none;Timeout=1;Pooling=false").Options));
        var r=await dead.ReadAsync(new(kind=="actor"?Guid.Empty:Guid.NewGuid(),new string('a',64),kind=="target"?Guid.Empty:Guid.NewGuid()),default);Assert.Equal(error,r.Error);Assert.Null(r.Data);
    }
    [FunctionalFact]
    public async Task GivenUnavailableProvider_WhenGetting_Then503WithoutPayload()
    {
        var dead=new RecordReadStore(new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=unavailable;Username=none;Timeout=1;Pooling=false").Options));
        var r=await dead.ReadAsync(new(Guid.NewGuid(),new string('a',64),Guid.NewGuid()),default);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);
    }
    [FunctionalTheory][InlineData("grant")][InlineData("member")][InlineData("profileCollection")]
    public async Task GivenAuthorityRemovedAfterSql_WhenGetting_ThenKeepOneSnapshotAndNextGetObservesRemoval(string kind)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var key=new ProtectionFixture();var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var a=await AssociationSetup.Items(fixture,foreign);
        var grant=await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,a.Collection);await Members(foreign,a.Collection,[a.Record.Id],[]);var p=await Profile(s,recipient,key,collections:[a.Collection.PublicId]);var request=s with{Verifier=await Selected(s,p.Id)};
        var interceptor=new RemoveAfterRead(fixture,kind,grant.Id,a.Collection.Id,p.Id);var r=await new RecordReadStore(new ProfileSetup.Factory(fixture,interceptor)).ReadAsync(new(s.Actor,request.Verifier,a.Record.PublicId),default);
        Assert.Null(r.Error);Assert.Equal(a.Record.PublicId,r.Data!.Record.RecordId);Assert.Equal(new[]{a.Collection.PublicId},r.Data.CollectionIds);Assert.Equal(1,interceptor.Readers);var next=await Read(request,a.Record.PublicId);Assert.Equal("not_found",next.Error);Assert.Null(next.Data);
    }
    [FunctionalTheory][InlineData(false,false)][InlineData(true,false)][InlineData(true,true)]
    public async Task GivenUnrelatedDeepInventory_WhenGettingOneTarget_ThenAnalyzeOnlyOneCandidateAndItsActualAncestry(bool selected,bool shared)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var unrelated=await ProfileSetup.Create(fixture,owner);var targetOwner=shared?unrelated:s;
        var root=await Folder(targetOwner);var child=await Folder(targetOwner,root.Id);var target=await Record(targetOwner,child.Id);Guid[] collections=[];
        if(shared){var a=await AssociationSetup.Items(fixture,targetOwner);await Members(targetOwner,a.Collection,[target.Id],[]);await AssociationSetup.Grant(fixture,targetOwner,owner,s,owner,a.Collection);collections=[a.Collection.PublicId];}
        var p=await Profile(s,owner,key,shared?[]:[target.PublicId],collections:collections);
        foreach(var account in new[]{s,unrelated}){long? parent=null;for(var depth=0;depth<50;depth++){var folder=await Folder(account,parent);await Record(account,folder.Id);parent=folder.Id;}}
        var capture=new CapturePlan();var r=await new RecordReadStore(new ProfileSetup.Factory(fixture,capture)).ReadAsync(new(s.Actor,selected?await Selected(s,p.Id):s.Verifier,target.PublicId),default);
        Assert.Null(r.Error);Assert.Equal(target.PublicId,r.Data!.Record.RecordId);Assert.Equal(1,capture.Readers);
        await using var db=fixture.CreateContext();await db.Database.OpenConnectionAsync();await using var command=db.Database.GetDbConnection().CreateCommand();command.CommandText="EXPLAIN (ANALYZE, FORMAT JSON) "+capture.Sql;foreach(var parameter in capture.Parameters)command.Parameters.Add(parameter.Clone());
        using var plan=JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);output.WriteLine(plan.RootElement.GetRawText());
        static IEnumerable<JsonElement> Nodes(JsonElement node){yield return node;if(node.TryGetProperty("Plans",out var children))foreach(var ch in children.EnumerateArray())foreach(var n in Nodes(ch))yield return n;}
        var nodes=Nodes(plan.RootElement[0].GetProperty("Plan")).ToArray();var candidates=Assert.Single(nodes,x=>x.TryGetProperty("Subplan Name",out var name)&&name.GetString()=="CTE candidates");var ancestry=Assert.Single(nodes,x=>x.TryGetProperty("Subplan Name",out var name)&&name.GetString()=="CTE ancestry");
        Assert.Equal(1d,candidates.GetProperty("Actual Rows").GetDouble());Assert.Equal(2d,ancestry.GetProperty("Actual Rows").GetDouble());
    }
    [FunctionalTheory][InlineData("revoked")][InlineData("wrongRecipient")][InlineData("unknownAccess")][InlineData("zeroRevision")][InlineData("unsafeRevision")][InlineData("grantTerminal")][InlineData("collectionTrash")][InlineData("collectionTerminal")][InlineData("ownerClosing")][InlineData("ownerTerminal")][InlineData("recordTrash")][InlineData("recordTerminal")][InlineData("ancestorTrash")][InlineData("ancestorTerminal")]
    public async Task GivenHiddenSharedAuthorityOrContent_WhenGetting_Then404BeforeNativeInspection(string kind)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var items=await AssociationSetup.Items(fixture,foreign);var included=await Record(foreign,items.Folder.Id);var grant=await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,items.Collection);await Members(foreign,items.Collection,[included.Id],[]);
        await using(var db=fixture.CreateContext()){var g=await db.CollectionGrants.SingleAsync(x=>x.Id==grant.Id);g.RecipientKeyEnvelope="bad"u8.ToArray();if(kind=="revoked")g.State=CollectionGrantState.Revoked;if(kind=="wrongRecipient")g.RecipientAccountId=foreign.InternalId;if(kind=="unknownAccess")g.Access=(CollectionGrantAccess)99;if(kind=="zeroRevision")g.Revision=0;if(kind=="unsafeRevision")g.Revision=ProtocolBinary.MaxInteger+1;if(kind=="collectionTrash")await db.Collections.Where(x=>x.Id==items.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.DeletedAt,DateTimeOffset.UtcNow));if(kind=="ownerClosing")await db.Accounts.Where(x=>x.Id==foreign.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));if(kind=="recordTrash")await db.Records.Where(x=>x.Id==included.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,DateTimeOffset.UtcNow));if(kind=="ancestorTrash")await db.Folders.Where(x=>x.Id==items.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.DeletedAt,DateTimeOffset.UtcNow));if(kind.EndsWith("Terminal",StringComparison.Ordinal))db.TerminalErasures.Add(new(){ResourceId=kind=="grantTerminal"?grant.PublicId:kind=="collectionTerminal"?items.Collection.PublicId:kind=="ownerTerminal"?foreign.AccountId:kind=="recordTerminal"?included.PublicId:items.Folder.PublicId,ResourceKind="fixture",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var r=await Read(s,included.PublicId);Assert.Equal("not_found",r.Error);Assert.Null(r.Data);
    }

    [FunctionalTheory][InlineData("blob")][InlineData("signature")][InlineData("owner")][InlineData("collection")][InlineData("grant")][InlineData("recipient")][InlineData("revision")][InlineData("epoch")][InlineData("collectionEnvelope")][InlineData("ownerPins")][InlineData("recipientPins")][InlineData("missingOwnerPins")][InlineData("missingRecipientPins")]
    public async Task GivenCorruptRelevantNativeGrant_WhenGetting_Then503WithoutPartialContent(string kind)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var items=await AssociationSetup.Items(fixture,foreign);var grant=await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,items.Collection);await Members(foreign,items.Collection,[items.Record.Id],[]);await Record(s);
        await using(var db=fixture.CreateContext()){var g=await db.CollectionGrants.SingleAsync(x=>x.Id==grant.Id);if(kind=="blob")g.RecipientKeyEnvelope="bad"u8.ToArray();if(kind is "owner" or "collection" or "grant" or "recipient")g.RecipientKeyEnvelope=Bytes(owner.WrapGrant(kind=="owner"?Guid.NewGuid():foreign.AccountId,kind=="collection"?Guid.NewGuid():items.Collection.PublicId,kind=="grant"?Guid.NewGuid():grant.PublicId,kind=="recipient"?Guid.NewGuid():s.Actor,recipient.Material.RecipientKey));if(kind=="signature"){var wrapper=JsonSerializer.Deserialize<RecipientEnvelope>(g.RecipientKeyEnvelope,ProtectionFixture.Json)!;ProtocolBinary.TryDecode(wrapper.Signature,null,out var bytes);bytes[0]^=1;g.RecipientKeyEnvelope=Bytes(wrapper with{Signature=ProtocolBinary.Encode(bytes)});}if(kind=="revision")g.Revision=2;if(kind=="epoch")await db.Collections.Where(x=>x.Id==items.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.KeyEpoch,2));if(kind=="collectionEnvelope")await db.Collections.Where(x=>x.Id==items.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,"bad"u8.ToArray()));if(kind is "ownerPins" or "recipientPins")await db.VaultProtections.Where(x=>x.AccountId==(kind=="ownerPins"?foreign.InternalId:s.InternalId)).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Material,"bad"u8.ToArray()));if(kind is "missingOwnerPins" or "missingRecipientPins")await db.VaultProtections.Where(x=>x.AccountId==(kind=="missingOwnerPins"?foreign.InternalId:s.InternalId)).ExecuteDeleteAsync();await db.SaveChangesAsync();}
        var before=await Snapshot(s);var foreignBefore=await Snapshot(foreign);var r=await Read(s,items.Record.PublicId);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));Assert.Equal(foreignBefore,await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData("missing","not_found")][InlineData("closing","not_found")][InlineData("terminal","not_found")][InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")][InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")][InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")][InlineData("nullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)]
    public async Task GivenCurrentAccountOrSessionState_WhenGetting_ThenRevalidateWithoutMutation(string kind,string? error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var record=await Record(s);await using(var db=fixture.CreateContext()){var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);if(kind=="closing")a.State=AccountState.ClosurePending;if(kind=="terminal")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="zeroPolicy")v.PolicyRevision=0;if(kind=="zeroGeneration")v.RevocationGeneration=0;if(kind is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(kind is "noExpiry" or "nullExpiry")v.ExpiresAt=null;await db.SaveChangesAsync();}var before=await Snapshot(s);var r=await Read(s with{Actor=kind=="missing"?Guid.NewGuid():s.Actor,Verifier=kind=="access"?new string('a',64):s.Verifier},record.PublicId);Assert.Equal(error,r.Error);if(error is null)Assert.Equal(record.PublicId,r.Data!.Record.RecordId);else Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("valid",null)][InlineData("dangling","vault_access_denied")][InlineData("foreign","vault_access_denied")][InlineData("trash","vault_access_denied")][InlineData("terminal","vault_access_denied")]
    public async Task GivenSelectedProfileLifecycle_WhenGetting_ThenRequireCurrentOwnedActiveSelection(string kind,string? error)
    {using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var foreign=await ProfileSetup.Create(fixture,owner);var own=await Record(s);var p=await Profile(kind=="foreign"?foreign:s,owner,scoped,kind=="foreign"?[]:[own.PublicId]);var verifier=await Selected(s,kind=="dangling"?long.MaxValue:p.Id);await using(var db=fixture.CreateContext()){if(kind=="trash")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));if(kind=="terminal"){db.TerminalErasures.Add(new(){ResourceId=p.PublicId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}var r=await Read(s with{Verifier=verifier},own.PublicId);Assert.Equal(error,r.Error);if(error is null)Assert.Equal(own.PublicId,r.Data!.Record.RecordId);else Assert.Null(r.Data);}

    [FunctionalFact]
    public async Task GivenExpiryImmediatelyBeforeSql_WhenGetting_ThenDatabaseTimeDeniesWithoutMutation()
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var record=await Record(s);var deadline=DateTimeOffset.UtcNow.AddSeconds(1);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));var before=await Snapshot(s);var r=await new RecordReadStore(new ProfileSetup.Factory(fixture,new ExpireBeforeRead(deadline))).ReadAsync(new(s.Actor,s.Verifier,record.PublicId),default);Assert.Equal("vault_access_denied",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));}

    [FunctionalFact]
    public async Task GivenUnavailableMembershipTable_WhenGetting_Then503AndRestoreDependency()
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var before=await Snapshot(s);await using var db=fixture.CreateContext();await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection_record RENAME TO fixture_unavailable_collection_record");try{var r=await Read(s,Guid.NewGuid());Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);}finally{await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_collection_record RENAME TO collection_record");}Assert.Equal(before,await Snapshot(s));}

    [FunctionalFact]
    public async Task GivenCancelledCaller_WhenGetting_ThenPropagateWithoutChangingState()
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var before=await Snapshot(s);using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().ReadAsync(new(s.Actor,s.Verifier,Guid.NewGuid()),ct.Token));Assert.Equal(before,await Snapshot(s));}

    [FunctionalFact]
    public async Task GivenSamePublicGuidAcrossResourceKinds_WhenGetting_ThenKeepTypedVisibleReferences()
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var id=Guid.NewGuid();var a=await AssociationSetup.Items(fixture,s,id);
        await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));
        await Members(s,a.Collection,[a.Record.Id],[a.Folder.Id]);var input=ProfileSetup.Input(s,owner,key);input=input with{ProfileId=id,KeyWrappers=input.KeyWrappers with{MasterKeyWrapper=owner.Wrap(s.AccountId,"profile",id,s.Actor)},RecordIds=[id],FolderIds=[id],CollectionIds=[id]};
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);
        var r=await Read(s,id);Assert.Null(r.Error);Assert.Equal(id,r.Data!.Record.RecordId);Assert.Equal(new[]{id},r.Data.ProfileIds);Assert.Equal(id,r.Data.FolderId);Assert.Equal(new[]{id},r.Data.CollectionIds);
    }
    [FunctionalTheory][InlineData("profileTrash")][InlineData("profileTerminal")][InlineData("collectionTrash")][InlineData("collectionTerminal")]
    public async Task GivenHiddenOwnedRelationship_WhenGettingAccountwide_ThenKeepRecordButOmitHiddenReference(string kind)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);await Members(s,a.Collection,[a.Record.Id],[]);var p=await Profile(s,owner,key,[a.Record.PublicId],collections:[a.Collection.PublicId]);
        await using(var db=fixture.CreateContext()){if(kind=="profileTrash")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));if(kind=="collectionTrash")await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.DeletedAt,DateTimeOffset.UtcNow));if(kind.EndsWith("Terminal")){db.TerminalErasures.Add(new(){ResourceId=kind.StartsWith("profile")?p.PublicId:a.Collection.PublicId,ResourceKind="fixture",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        var r=await Read(s,a.Record.PublicId);Assert.Null(r.Error);Assert.Equal(kind.StartsWith("profile")?[]:new[]{p.PublicId},r.Data!.ProfileIds);Assert.Equal(kind.StartsWith("collection")?[]:new[]{a.Collection.PublicId},r.Data.CollectionIds);
    }
    [FunctionalTheory][InlineData(true,"epoch")][InlineData(false,"epoch")][InlineData(true,"generation")][InlineData(false,"generation")][InlineData(true,"zeroRevision")][InlineData(false,"unsafeRevision")]
    public async Task GivenInconsistentCurrentProtectionPins_WhenGettingSharedTarget_Then503WithoutPartialReferences(bool ownerPins,string kind)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var a=await AssociationSetup.Items(fixture,foreign);await Members(foreign,a.Collection,[a.Record.Id],[]);await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,a.Collection);
        await using(var db=fixture.CreateContext()){var p=await db.VaultProtections.SingleAsync(x=>x.AccountId==(ownerPins?foreign.InternalId:s.InternalId));if(kind=="epoch")p.KeyEpoch++;if(kind=="generation")p.RecoveryGeneration++;if(kind=="zeroRevision")p.Revision=0;if(kind=="unsafeRevision")p.Revision=ProtocolBinary.MaxInteger+1;await db.SaveChangesAsync();}
        var r=await Read(s,a.Record.PublicId);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);
    }
    [FunctionalTheory]
    [InlineData("numericString",false,"visible")][InlineData("numericString",true,"visible")]
    [InlineData("unknown",false,"visible")][InlineData("unknown",true,"visible")]
    [InlineData("duplicate",false,"visible")][InlineData("duplicate",true,"visible")]
    [InlineData("case",false,"visible")][InlineData("case",true,"visible")]
    [InlineData("numericString",true,"unselected")][InlineData("numericString",false,"ancestorTrash")]
    [InlineData("numericString",true,"revoked")][InlineData("numericString",false,"recordTrash")]
    public async Task GivenAlternateStoredCollectionEnvelopeShape_WhenGettingSharedTarget_ThenRejectRelevantEvidenceButKeepHiddenTargetsNonrevealing(string shape,bool selected,string visibility)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var key=new ProtectionFixture();
        var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var a=await AssociationSetup.Items(fixture,foreign);var target=await Record(foreign,a.Folder.Id);
        await Members(foreign,a.Collection,[target.Id],[]);var grant=await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,a.Collection);
        var p=await Profile(s,recipient,key,collections:visibility=="unselected"?[]:[a.Collection.PublicId]);var request=selected?s with{Verifier=await Selected(s,p.Id)}:s;
        var original=System.Text.Encoding.UTF8.GetString(a.Collection.Envelope);var malformed=shape switch{
            "numericString"=>original.Replace("\"keyEpoch\":1","\"keyEpoch\":\"1\""),
            "unknown"=>original.Replace("{","{\"secret\":1,"),"duplicate"=>original.Replace("{","{\"keyEpoch\":1,"),_=>original.Replace("\"format\"","\"Format\"")};
        Assert.NotEqual(original,malformed);
        await using(var db=fixture.CreateContext())
        {
            await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,System.Text.Encoding.UTF8.GetBytes(malformed)));
            if(visibility=="ancestorTrash")await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.DeletedAt,DateTimeOffset.UtcNow));
            if(visibility=="recordTrash")await db.Records.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,DateTimeOffset.UtcNow));
            if(visibility=="revoked")await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Revoked));
        }
        var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);var result=await Read(request,target.PublicId);
        Assert.Equal(visibility=="visible"?"persistence_unavailable":"not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));Assert.Equal(ownerBefore,await Snapshot(foreign));
    }
    private RecordReadStore Store()=>new(fixture);
    private Task<VaultResult<RecordReadDetails>> Read(ProfileSetup.State s,Guid target)=>Store().ReadAsync(new(s.Actor,s.Verifier,target),default);
    private async Task<VaultRecord> Record(ProfileSetup.State s,long? folder=null){var row=new VaultRecord{PublicId=Guid.NewGuid(),AccountId=s.InternalId,FolderId=folder,Envelope=Bytes(ProfileSetup.Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.CreateContext();db.Records.Add(row);await db.SaveChangesAsync();return row;}
    private async Task<VaultFolder> Folder(ProfileSetup.State s,long? parent=null){var row=new VaultFolder{PublicId=Guid.NewGuid(),AccountId=s.InternalId,ParentFolderId=parent,Envelope=Bytes(ProfileSetup.Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.CreateContext();db.Folders.Add(row);await db.SaveChangesAsync();return row;}
    private async Task<Profile> Profile(ProfileSetup.State s,ProtectionFixture owner,ProtectionFixture scoped,Guid[]? records=null,Guid[]? folders=null,Guid[]? collections=null){var input=ProfileSetup.Input(s,owner,scoped) with{RecordIds=records??[],FolderIds=folders??[],CollectionIds=collections??[]};Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);await using var db=fixture.CreateContext();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==input.ProfileId);}
    private async Task Members(ProfileSetup.State s,VaultCollection collection,long[] records,long[] folders){await using var db=fixture.CreateContext();db.CollectionRecords.AddRange(records.Select(x=>new CollectionRecord{AccountId=s.InternalId,CollectionId=collection.Id,RecordId=x}));db.CollectionFolders.AddRange(folders.Select(x=>new CollectionFolder{AccountId=s.InternalId,CollectionId=collection.Id,FolderId=x}));await db.SaveChangesAsync();}
    private async Task<string> Selected(ProfileSetup.State s,long profile){var verifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));await using var db=fixture.CreateContext();db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,ProfileId=profile,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return verifier;}
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private async Task<string> Snapshot(ProfileSetup.State s){await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),ProfileRecords=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync(),ProfileFolders=await db.ProfileFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileCollections=await db.ProfileCollections.AsNoTracking().Where(x=>db.Profiles.Any(p=>p.Id==x.ProfileId && p.AccountId==s.InternalId)).OrderBy(x=>x.ProfileId).ThenBy(x=>x.CollectionId).ToArrayAsync(),CollectionRecords=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),CollectionFolders=await db.CollectionFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.FolderId).ToArrayAsync(),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>x.RecipientAccountId==s.InternalId || db.Collections.Any(c=>c.Id==x.CollectionId && c.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync()});}
    private sealed class CapturePlan:DbCommandInterceptor
    {
        public int Readers{get;private set;}public string Sql{get;private set;}="";public NpgsqlParameter[] Parameters{get;private set;}=[];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {Readers++;Sql=command.CommandText;Parameters=command.Parameters.Cast<NpgsqlParameter>().Select(x=>x.Clone()).ToArray();return ValueTask.FromResult(result);}
    }

    private sealed class ExpireBeforeRead(DateTimeOffset deadline):DbCommandInterceptor
    {public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> result,CancellationToken ct=default){var wait=deadline-DateTimeOffset.UtcNow;if(wait>TimeSpan.Zero)await Task.Delay(wait+TimeSpan.FromMilliseconds(100),ct);return result;}}
    private sealed class RemoveAfterRead(PostgresFixture fixture,string kind,long grant,long collection,long profile):DbCommandInterceptor
    {
        public int Readers{get;private set;}
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData e,DbDataReader reader,CancellationToken ct=default)
        {
            Readers++;if(Readers==1){await using var db=fixture.CreateContext();if(kind=="grant")await db.CollectionGrants.Where(x=>x.Id==grant).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Revoked),ct);if(kind=="member")await db.CollectionRecords.Where(x=>x.CollectionId==collection).ExecuteDeleteAsync(ct);if(kind=="profileCollection")await db.ProfileCollections.Where(x=>x.ProfileId==profile&&x.CollectionId==collection).ExecuteDeleteAsync(ct);}return reader;
        }
    }
}
