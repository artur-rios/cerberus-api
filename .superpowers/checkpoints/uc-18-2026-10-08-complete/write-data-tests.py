from pathlib import Path
r=Path('/tmp/cerberus-uc18-worktree');src=(r/'tests/Infrastructure/ArturRios.Cerberus.Data.Tests/RecordListStoreTests.cs').read_text()
header=src[:src.index('    [FunctionalFact]')].replace('RecordListStoreTests','RecordReadStoreTests')
# Keep real native grant/state fixtures and failure matrices; adapt their observable
# assertions to one target rather than list pagination.
def method(name):
 pos=src.index('public async Task '+name);start=src.rfind('    [Functional',0,pos);end=src.find('    [Functional',pos)
 if end<0:end=src.index('    private ',pos)
 return src[start:end]
selected=[]
t=method('GivenHiddenSharedAuthorityOrContent_').replace('WhenListing_ThenOmitBeforePaginationWithoutNativeInspection','WhenGetting_Then404BeforeNativeInspection')
t=t.replace('await List(s,1)','await Read(s,included.PublicId)').replace('Assert.Null(r.Error);Assert.Empty(r.Data!.Items);Assert.False(r.Data.HasMore);Assert.Equal(0,r.Data.Boundary);','Assert.Equal("not_found",r.Error);Assert.Null(r.Data);');selected.append(t)
t=method('GivenCorruptRelevantNativeGrant_').replace('WhenListing','WhenGetting').replace('await List(s,1)','await Read(s,items.Record.PublicId)');selected.append(t)
t=method('GivenCurrentAccountOrSessionState_').replace('WhenListing','WhenGetting').replace('Verifier=kind=="access"?new string(\'a\',64):s.Verifier});','Verifier=kind=="access"?new string(\'a\',64):s.Verifier},record.PublicId);').replace('await List(','await Read(').replace('Assert.Single(r.Data!.Items).RecordId','r.Data!.Record.RecordId');selected.append(t)
t=method('GivenSelectedProfileLifecycle_').replace('WhenListing','WhenGetting').replace('await List(s with{Verifier=verifier})','await Read(s with{Verifier=verifier},own.PublicId)').replace('Assert.Single(r.Data!.Items).RecordId','r.Data!.Record.RecordId');selected.append(t)
t=method('GivenExpiryImmediatelyBeforeSql_').replace('WhenListing','WhenGetting').replace('await Record(s);','var record=await Record(s);').replace('RecordListStore','RecordReadStore').replace('.ListAsync(new(s.Actor,s.Verifier,10,0,null)', '.ReadAsync(new(s.Actor,s.Verifier,record.PublicId)');selected.append(t)
t=method('GivenUnavailableMembershipTable_').replace('WhenListing','WhenGetting').replace('await List(s)','await Read(s,Guid.NewGuid())');selected.append(t)
t=method('GivenCancelledCaller_').replace('WhenListing','WhenGetting').replace('Store().ListAsync(new(s.Actor,s.Verifier,10,0,null)','Store().ReadAsync(new(s.Actor,s.Verifier,Guid.NewGuid())');selected.append(t)
custom='''    [FunctionalFact]
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
        var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);var r=await Read(selected?s with{Verifier=await Selected(s,p.Id)}:s,record.PublicId);
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
'''
helpers=src[src.index('    private async Task<VaultRecord> Record'):src.index('    private sealed class RevokeAfterRead')]
capture=src[src.index('    private sealed class CapturePlan'):src.index('    private RecordListStore Store')]
expire=src[src.index('    private sealed class ExpireBeforeRead'):src.rfind('\n}')]
race='''    private sealed class RemoveAfterRead(PostgresFixture fixture,string kind,long grant,long collection,long profile):DbCommandInterceptor
    {
        public int Readers{get;private set;}
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData e,DbDataReader reader,CancellationToken ct=default)
        {
            Readers++;if(Readers==1){await using var db=fixture.CreateContext();if(kind=="grant")await db.CollectionGrants.Where(x=>x.Id==grant).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Revoked),ct);if(kind=="member")await db.CollectionRecords.Where(x=>x.CollectionId==collection).ExecuteDeleteAsync(ct);if(kind=="profileCollection")await db.ProfileCollections.Where(x=>x.ProfileId==profile&&x.CollectionId==collection).ExecuteDeleteAsync(ct);}return reader;
        }
    }
'''
(r/'tests/Infrastructure/ArturRios.Cerberus.Data.Tests/RecordReadStoreTests.cs').write_text(header+custom+'\n'.join(selected)+'    private RecordReadStore Store()=>new(fixture);\n    private Task<VaultResult<RecordReadDetails>> Read(ProfileSetup.State s,Guid target)=>Store().ReadAsync(new(s.Actor,s.Verifier,target),default);\n'+helpers+capture+expire+'\n'+race+'}\n')
