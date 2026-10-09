from pathlib import Path
r=Path('/tmp/cerberus-uc18-worktree');src=(r/'tests/Presentation/ArturRios.Cerberus.WebApi.Tests/RecordListHttpTests.cs').read_text()
header=src[:src.index('    [FunctionalFact]')].replace('RecordListHttpTests','RecordReadHttpTests')
helpers=src[src.index('    private async Task<RecordCreateInput> Create'):]
def method(name):
 pos=src.index('public async Task '+name);start=src.rfind('    [Functional',0,pos);end=src.find('    [Functional',pos)
 if end<0:end=src.index('    private ',pos)
 return src[start:end]
blocks=[]
for name in ['GivenMalformedAccessOrGetBody_','GivenRawEmptyHeaderOrUnknownLengthBody_','GivenInvalidCurrentIdentity_','GivenInvalidCurrentAccountOrSession_','GivenSelectedProfileBecomesHidden_']:
 t=method(name).replace('WhenListing','WhenGetting').replace('"/api/records"','"/api/records/11111111-1111-1111-1111-111111111111"');blocks.append(t)
t=method('GivenCorruptRelevantNativeGrant_').replace('WhenListing_Then503WithoutAnyOwnedOrSharedPayload','WhenGetting_Then503WithoutAnyPayload').replace('Read(s,"/api/records")','Read(s,"/api/records/"+shared.RecordId)');blocks.append(t)
t=method('GivenHiddenAncestorOrOwnerWithBadNativeWrapper_') if 'GivenHiddenAncestorOrOwnerWithBadNativeWrapper_' in src else ''
# Pick the existing hidden-wrapper behavior by its unique observable assertion.
if not t:
 pos=src.index('var page=await Success<RecordListOutput>(response,200);Assert.Empty(page.Items);Assert.Null(page.NextCursor);');start=src.rfind('    [Functional',0,pos);end=src.index('    private ',pos);t=src[start:end]
t=t.replace('WhenListing','WhenGetting').replace('Read(s,"/api/records")','Read(s,"/api/records/"+shared.RecordId)').replace('var page=await Success<RecordListOutput>(response,200);Assert.Empty(page.Items);Assert.Null(page.NextCursor);','await Failure(response,404,"not_found");');blocks.append(t)
custom='''    [FunctionalFact]
    public async Task GivenHttpCreatedOwnedRecord_WhenGetting_ThenReturnEightFieldsAndCurrentDirectRelationshipsWithoutMutation()
    {
        using var owner=new ProtectionFixture();using var key1=new ProtectionFixture();using var key2=new ProtectionFixture();var s=await Setup(owner);var root=await Folder(s);var child=await Folder(s,root.Id);
        var p=await Profile(s,owner,key1);var q=await Profile(s,owner,key2);var record=await Create(s,[p.PublicId,q.PublicId],child.PublicId);var before=await Snapshot(s);
        using var response=await Read(s,"/api/records/"+record.RecordId);var result=await Success<RecordDetailsOutput>(response,200);Fields(result,"recordId","revision","serverSequence","editedAt","envelope","profileIds","folderId","collectionIds");
        Assert.Equal(record.RecordId,result.RecordId);Assert.Equal(record.Envelope,result.Envelope);Assert.Equal(Normalize(record.EditedAt),result.EditedAt);Assert.Equal(new[]{p.PublicId,q.PublicId}.Order(),result.ProfileIds);Assert.Equal(child.PublicId,result.FolderId);Assert.Empty(result.CollectionIds);
        await using var db=fixture.Context();var persisted=await db.Records.AsNoTracking().SingleAsync(x=>x.PublicId==record.RecordId);Assert.Equal(persisted.Revision,result.Revision);Assert.Equal(persisted.ServerSequence,result.ServerSequence);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("Master",false)][InlineData("Master",true)][InlineData("PerProfile",false)][InlineData("PerProfile",true)]
    public async Task GivenNativeSelectedProfile_WhenGetting_ThenHideOtherProfilesAndParentWithoutFolderRoute(string mode,bool folderRoute)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();using var other=new ProtectionFixture();var s=await Setup(owner);var root=await Folder(s);var child=await Folder(s,root.Id);
        var p=await Profile(s,owner,scoped,folderRoute?root:null,mode);var q=await Profile(s,owner,other);var record=await Create(s,[p.PublicId,q.PublicId],child.PublicId);var outside=await Create(s,[q.PublicId]);var descendant=await Create(s,folder:child.PublicId);
        var selected=s with{Access=await Open(s,p,scoped)};var before=await Snapshot(s);using var response=await Read(selected,"/api/records/"+record.RecordId);var result=await Success<RecordDetailsOutput>(response,200);
        Assert.Equal(new[]{p.PublicId},result.ProfileIds);Assert.Equal(folderRoute?child.PublicId:null,result.FolderId);Assert.Empty(result.CollectionIds);Assert.Equal(record.Envelope,result.Envelope);
        using var denied=await Read(selected,"/api/records/"+outside.RecordId);await Failure(denied,404,"not_found");using var dynamic=await Read(selected,"/api/records/"+descendant.RecordId);
        if(folderRoute){var item=await Success<RecordDetailsOutput>(dynamic,200);Assert.Empty(item.ProfileIds);Assert.Equal(child.PublicId,item.FolderId);}else await Failure(dynamic,404,"not_found");Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly,false,false)][InlineData(CollectionGrantAccess.ReadOnly,true,false)][InlineData(CollectionGrantAccess.ReadOnly,true,true)]
    [InlineData(CollectionGrantAccess.ReadWrite,false,true)][InlineData(CollectionGrantAccess.ReadWrite,true,false)][InlineData(CollectionGrantAccess.ReadWrite,true,true)]
    public async Task GivenNativeRecipientGrant_WhenGetting_ThenExposeOnlyIncludedCurrentRecipientRelationships(CollectionGrantAccess access,bool folderRoute,bool selectedScope)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var ownerKey=new ProtectionFixture();using var recipientKey=new ProtectionFixture();var foreign=await Setup(owner);var s=await Setup(recipient);var root=await Folder(foreign);var child=await Folder(foreign,root.Id);var ownerProfile=await Profile(foreign,owner,ownerKey,root);
        var shared=await Create(foreign,[ownerProfile.PublicId],child.PublicId);var outside=await Create(foreign);var(collection,grant)=await Share(foreign,owner,s,recipient,access,folderRoute?[]:[shared.RecordId],folderRoute?[root.Id]:[]);
        var p=await Profile(s,recipient,recipientKey,collections:[collection.PublicId]);var request=selectedScope?s with{Access=await Open(s,p,recipientKey)}:s;var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);
        using var response=await Read(request,"/api/records/"+shared.RecordId);var item=await Success<RecordDetailsOutput>(response,200);Assert.Empty(item.ProfileIds);Assert.Equal(new[]{collection.PublicId},item.CollectionIds);Assert.Equal(folderRoute?child.PublicId:null,item.FolderId);Assert.Equal(shared.Envelope,item.Envelope);
        using var hidden=await Read(request,"/api/records/"+outside.RecordId);await Failure(hidden,404,"not_found");Assert.Equal(before,await Snapshot(s));Assert.Equal(ownerBefore,await Snapshot(foreign));
        await using(var db=fixture.Context())await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Revoked));var after=await Snapshot(s);using var revoked=await Read(request,"/api/records/"+shared.RecordId);await Failure(revoked,404,"not_found");Assert.Equal(after,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("bad")][InlineData("zero")][InlineData("uppercase")][InlineData("compact")][InlineData("braced")][InlineData("urn")]
    public async Task GivenNoncanonicalPublicId_WhenGetting_Then400WithoutMutation(string kind)
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var id=Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");var text=kind switch{"bad"=>"bad","zero"=>Guid.Empty.ToString(),"uppercase"=>id.ToString().ToUpperInvariant(),"compact"=>id.ToString("N"),"braced"=>id.ToString("B"),_=>"urn:uuid:"+id};var before=await Snapshot(s);using var r=await Read(s,"/api/records/"+text);await Failure(r,400,"validation_failed");Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("?owner=other")][InlineData("?profileId=other")][InlineData("?pageSize=1")][InlineData("?cursor=opaque")][InlineData("?search=secret")][InlineData("?id=other")]
    public async Task GivenAnyQueryOverride_WhenGetting_Then400WithoutMutation(string query)
    {using var owner=new ProtectionFixture();var s=await Setup(owner);var before=await Snapshot(s);using var r=await Read(s,"/api/records/11111111-1111-1111-1111-111111111111"+query);await Failure(r,400,"validation_failed");Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("envelope")][InlineData("revision")][InlineData("zero")][InlineData("time")][InlineData("unavailable")]
    public async Task GivenCorruptVisibleContentOrDependency_WhenGetting_Then503WithoutPartialPayload(string kind)
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);await using var db=fixture.Context();
        if(kind=="envelope")await db.Records.Where(x=>x.PublicId==target.RecordId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,new byte[]{1}));if(kind=="revision")await db.Records.Where(x=>x.PublicId==target.RecordId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Revision,0));if(kind=="zero")await db.Records.Where(x=>x.PublicId==target.RecordId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.ServerSequence,0));if(kind=="time")await db.Records.Where(x=>x.PublicId==target.RecordId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.EditedAt,DateTimeOffset.MinValue));if(kind=="unavailable")await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection_record RENAME TO http_fixture_unavailable_collection_record");
        var before=await Snapshot(s,kind=="unavailable");try{using var response=await Read(s,"/api/records/"+target.RecordId);await Failure(response,503,"persistence_unavailable");}finally{if(kind=="unavailable")await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.http_fixture_unavailable_collection_record RENAME TO collection_record");}Assert.Equal(before,await Snapshot(s,kind=="unavailable"));
    }
    [FunctionalTheory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task GivenCyclicOrHiddenAncestry_WhenGetting_ThenDistinguishRelevant503FromHidden404(bool hidden,bool selectedScope)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await Setup(owner);var root=await Folder(s);var child=await Folder(s,root.Id);var p=await Profile(s,owner,key,root);var target=await Create(s,[p.PublicId],child.PublicId);var request=selectedScope?s with{Access=await Open(s,p,key)}:s;
        await using(var db=fixture.Context())await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,child.Id).SetProperty(f=>f.DeletedAt,hidden?(DateTimeOffset?)DateTimeOffset.UtcNow:null));var before=await Snapshot(s);using var response=await Read(request,"/api/records/"+target.RecordId);await Failure(response,hidden?404:503,hidden?"not_found":"persistence_unavailable");Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenUnknownTarget_WhenGetting_ThenNonrevealing404WithoutMutation()
    {using var owner=new ProtectionFixture();var s=await Setup(owner);var before=await Snapshot(s);using var r=await Read(s,"/api/records/"+Guid.NewGuid());await Failure(r,404,"not_found");Assert.Equal(before,await Snapshot(s));}
'''
(r/'tests/Presentation/ArturRios.Cerberus.WebApi.Tests/RecordReadHttpTests.cs').write_text(header+custom+'\n'.join(blocks)+helpers)
