from pathlib import Path
s=Path('tests/Presentation/ArturRios.Cerberus.WebApi.Tests/RecordTrashHttpTests.cs').read_text()
header=s[:s.index('    [FunctionalTheory]')].replace('RecordTrashHttpTests','RecordPermanentDeleteHttpTests')
header=header.replace('using ArturRios.Cerberus.Command.Profiles;', 'using ArturRios.Cerberus.Data.Erasure;\nusing ArturRios.Cerberus.Domain.Operations;\nusing ArturRios.Cerberus.Shared.Operations;\nusing Microsoft.Extensions.DependencyInjection;\nusing Microsoft.Extensions.DependencyInjection.Extensions;\nusing ArturRios.Cerberus.Command.Profiles;')
def method(name):
 start=s.rfind('    [Functional',0,s.index('public async Task '+name))
 ends=[n for n in [s.find('\n    [Functional',start+5),s.find('\n    private ',start+5)] if n>=0]
 return s[start:min(ends)]
names=['GivenActualNativeVisibleForeignScope_WhenDeletingStaleCorruptTarget_ThenReadOnlyAndReadWriteBoth403','GivenStrictOrForgedDeleteBody_WhenTrashing_Then400WithoutAnyOperation','GivenSelectedCollectionLinkRemovedAfterUnlock_WhenTrashingForeignTarget_Then404WithoutExpandingScope','GivenCorruptMetadataOrPersistenceOutage_WhenTrashing_Then503WithoutPartialOperation','GivenCancelledHttpCaller_WhenTrashing_ThenPropagateAndPreserveRecord','GivenHiddenTargetWithBadCipherAndStaleRevision_WhenTrashing_ThenNonrevealing404WithoutMutation','GivenNoncanonicalRoute_WhenTrashing_Then400WithoutMutation','GivenInvalidTransportOrAccess_WhenTrashing_ThenRejectWithoutMutation','GivenRawEmptyAccessHeader_WhenTrashing_Then400','GivenInvalidCurrentIdentity_WhenTrashing_ThenRejectWithoutPersistence','GivenInvalidCurrentActorState_WhenTrashing_ThenNoMutation','GivenCommittedSharedRelationshipChange_WhenTrashing_ThenCurrentPermissionWins','GivenRelevantMalformedNativeAuthority_WhenTrashing_Then503WithoutAnyContentChange']
generic='\n'.join(method(n) for n in names)
generic=generic.replace('Trash(', 'Permanent(').replace('Delete(', 'DeletePermanent(').replace('WhenTrashing','WhenPermanentlyDeleting').replace('WhenDeleting','WhenPermanentlyDeleting')
generic=generic.replace('[InlineData("purgeWithoutTrash")]','')
generic=generic.replace('c.Request.Path="/api/records/"+Guid.NewGuid()','c.Request.Path="/api/records/"+Guid.NewGuid()+"/permanent"')
helpers=s[s.index('    private async Task<RecordCreateInput> Create'):]
helpers=helpers.replace('private static DateTimeOffset Normalize(DateTimeOffset value)=>value.AddTicks(-(value.Ticks%10));','')
helpers=helpers[:helpers.rfind('}')]
common='''    private static HttpRequestMessage DeletePermanent(State s,string path,byte[] body){var split=path.IndexOf('?');var uri=split<0?path+"/permanent":path[..split]+"/permanent"+path[split..];var r=Request(s,body,uri);r.Method=HttpMethod.Delete;return r;}
    private async Task<HttpResponseMessage> Permanent(State s,Guid id,long revision=1){using var req=DeletePermanent(s,"/api/records/"+id,Bytes(new{expectedRevision=revision}));return await Gateway.Client.SendAsync(req);}
    private async Task<VaultRecord> Row(Guid id){await using var db=fixture.Context();return await db.Records.AsNoTracking().SingleAsync(x=>x.PublicId==id);}
    private async Task<VaultCollection> OwnedCollection(State s,Guid target){await using var db=fixture.Context();var c=new VaultCollection{AccountId=s.InternalId,PublicId=Guid.NewGuid(),Envelope=Bytes(Envelope()),EditedAt=DateTimeOffset.UnixEpoch};db.Collections.Add(c);await db.SaveChangesAsync();var r=await db.Records.SingleAsync(x=>x.PublicId==target);db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=c.Id,RecordId=r.Id});await db.SaveChangesAsync();return c;}
    private async Task Trash(State s,Guid id){using var req=Request(s,Bytes(new{expectedRevision=1}),"/api/records/"+id);req.Method=HttpMethod.Delete;using var response=await Gateway.Client.SendAsync(req);await Success<DeleteRecordOutput>(response,200);}
    private async Task Erased(Guid id,PermanentlyDeleteRecordOutput output)
    {
        Fields(output,"recordId","deletedAt");Assert.Equal(id,output.RecordId);Assert.Equal(TimeSpan.Zero,output.DeletedAt.Offset);Assert.True(output.DeletedAt.Ticks>=10);Assert.Equal(0,output.DeletedAt.Ticks%10);
        await using var db=fixture.Context();Assert.False(await db.Records.AnyAsync(x=>x.PublicId==id));Assert.False(await db.TrashEntries.AnyAsync(x=>x.ResourceKind=="record" && x.ResourceId==id));
        Assert.Equal(output.DeletedAt,(await db.TerminalErasures.SingleAsync(x=>x.ResourceKind=="record" && x.ResourceId==id)).DeletedAt);
        Assert.NotNull((await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="record-purge/"+id)).CompletedAt);
        var ledger=new FileErasureLedger(Environment.GetEnvironmentVariable("CERBERUS_ERASURE_LEDGER_PATH")!);var entries=new List<ErasureEntry>();
        await foreach(var entry in ledger.ReadAsync(default))if(entry.ResourceKind=="record" && entry.ResourceId==id)entries.Add(entry);
        Assert.Equal(new ErasureEntry(id,"record",output.DeletedAt),Assert.Single(entries));
    }
    private sealed class UnavailableLedger:IErasureLedger
    {
        public Task RecordAsync(ErasureEntry entry,CancellationToken ct)=>throw new IOException("fixture ledger outage");
        public async IAsyncEnumerable<ErasureEntry> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct){ct.ThrowIfCancellationRequested();await Task.CompletedTask;yield break;}
    }
'''
owned='''    [FunctionalTheory]
    [InlineData("wide",false,false)][InlineData("Master",false,false)][InlineData("PerProfile",false,false)][InlineData("Master",true,false)][InlineData("PerProfile",true,false)]
    [InlineData("wide",false,true)][InlineData("Master",false,true)][InlineData("PerProfile",false,true)][InlineData("Master",true,true)][InlineData("PerProfile",true,true)]
    public async Task GivenActualNativeOwnedActiveOrTrashedProfileScope_WhenPermanentlyDeleting_ThenDurableErasureAndExactlyTwoMetadataFields(string mode,bool folderRoute,bool trashed)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner);var root=await Folder(s);var child=await Folder(s,root.Id);
        var p=await Profile(s,owner,scoped,folderRoute?root:null,mode=="wide"?"Master":mode);var target=await Create(s,folderRoute?[]:[p.PublicId],child.PublicId);
        if(trashed)await Trash(s,target.RecordId);var actor=mode=="wide"?s:s with{Access=await Open(s,p,scoped)};var protectedBefore=await Protected(s);
        using var response=await Permanent(actor,target.RecordId,trashed?2:1);Assert.Contains("record_permanently_deleted",await response.Content.ReadAsStringAsync());await Erased(target.RecordId,await Success<PermanentlyDeleteRecordOutput>(response,200));Assert.Equal(protectedBefore,await Protected(s));
        using var get=await Read(actor,"/api/records/"+target.RecordId);await Failure(get,404,"not_found");using var list=await Read(actor,"/api/records");Assert.DoesNotContain((await Success<RecordListOutput>(list,200)).Items,x=>x.RecordId==target.RecordId);
        await using var db=fixture.Context();Assert.True(await db.Folders.AnyAsync(x=>x.Id==root.Id));Assert.True(await db.Folders.AnyAsync(x=>x.Id==child.Id));
        var handle=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==Hash(actor.Access));Assert.Equal(mode=="wide"?null:p.Id,handle.ProfileId);Assert.False(handle.Revoked);
    }
    [FunctionalTheory][InlineData("Master",false)][InlineData("PerProfile",false)][InlineData("Master",true)][InlineData("PerProfile",true)]
    public async Task GivenNativeSelectedOwnedCollection_WhenPermanentlyDeletingActiveOrTrash_ThenKeepCollectionAndProfileAuthority(string mode,bool trashed)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);var c=await OwnedCollection(s,target.RecordId);
        var p=await Profile(s,owner,scoped,mode:mode,collections:[c.PublicId]);if(trashed)await Trash(s,target.RecordId);var actor=s with{Access=await Open(s,p,scoped)};
        await using var db=fixture.Context();var before=await db.Collections.AsNoTracking().SingleAsync(x=>x.Id==c.Id);
        using var response=await Permanent(actor,target.RecordId,trashed?2:1);await Erased(target.RecordId,await Success<PermanentlyDeleteRecordOutput>(response,200));
        var after=await db.Collections.AsNoTracking().SingleAsync(x=>x.Id==c.Id);Assert.Equal(before.Revision+(trashed?0:1),after.Revision);Assert.Equal(before.Envelope,after.Envelope);Assert.Equal(before.EditedAt,after.EditedAt);
        Assert.Equal(Bytes(p),Bytes(await db.Profiles.AsNoTracking().SingleAsync(x=>x.Id==p.Id)));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenDamagedOwnedCipherAndMaximumRevision_WhenPermanentlyDeleting_ThenNoContentParsingOrTargetRevisionAdvance(bool trashed)
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);if(trashed)await Trash(s,target.RecordId);
        await using var db=fixture.Context();await db.Records.Where(x=>x.PublicId==target.RecordId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,"bad"u8.ToArray()).SetProperty(v=>v.Revision,ProtocolBinary.MaxInteger));
        using var response=await Permanent(s,target.RecordId,ProtocolBinary.MaxInteger);await Erased(target.RecordId,await Success<PermanentlyDeleteRecordOutput>(response,200));
    }
    [FunctionalFact]
    public async Task GivenRecordGuidAlsoUsedByFolderAndCollection_WhenPermanentlyDeleting_ThenKeepOtherKindsAndRejectRecreationAndLostSuccessRetry()
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);var folder=await Folder(s);var collection=await OwnedCollection(s,target.RecordId);
        await using var db=fixture.Context();await db.Folders.Where(x=>x.Id==folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.PublicId,target.RecordId));await db.Collections.Where(x=>x.Id==collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.PublicId,target.RecordId));
        using(var response=await Permanent(s,target.RecordId))await Erased(target.RecordId,await Success<PermanentlyDeleteRecordOutput>(response,200));
        var before=await Snapshot(s);using(var retry=await Permanent(s,target.RecordId))await Failure(retry,404,"not_found");Assert.Equal(before,await Snapshot(s));
        using(var recreate=await Send(s,target))await Failure(recreate,409,"revision_conflict");
        using(var edit=Request(s,Bytes(new RecordUpdateInput(1,Envelope(),DateTimeOffset.UnixEpoch)),"/api/records/"+target.RecordId)){edit.Method=HttpMethod.Put;using var response=await Gateway.Client.SendAsync(edit);await Failure(response,404,"not_found");}
        using(var move=Request(s,Bytes(new{expectedRevision=1,folderId=(Guid?)null}),"/api/records/"+target.RecordId+"/folder")){move.Method=HttpMethod.Put;using var response=await Gateway.Client.SendAsync(move);await Failure(response,404,"not_found");}
        using(var trash=Request(s,Bytes(new{expectedRevision=1}),"/api/records/"+target.RecordId)){trash.Method=HttpMethod.Delete;using var response=await Gateway.Client.SendAsync(trash);await Failure(response,404,"not_found");}
        Assert.True(await db.Folders.AnyAsync(x=>x.PublicId==target.RecordId));Assert.True(await db.Collections.AnyAsync(x=>x.PublicId==target.RecordId));
    }
    [FunctionalFact]
    public async Task GivenActualPutWins_WhenPermanentlyDeletingOldRevision_Then409AndCurrentRevisionCanBeErased()
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);
        using(var edit=Request(s,Bytes(new RecordUpdateInput(1,Envelope(),DateTimeOffset.UnixEpoch)),"/api/records/"+target.RecordId)){edit.Method=HttpMethod.Put;using var response=await Gateway.Client.SendAsync(edit);await Success<UpdateRecordOutput>(response,200);}
        var before=await Snapshot(s);using(var response=await Permanent(s,target.RecordId))await Failure(response,409,"revision_conflict");Assert.Equal(before,await Snapshot(s));
        using var current=await Permanent(s,target.RecordId,2);await Erased(target.RecordId,await Success<PermanentlyDeleteRecordOutput>(current,200));
    }
    [FunctionalFact]
    public async Task GivenLedgerOutageAfterCommittedIntent_WhenPermanentlyDeletingOverHttp_Then503KeepsTerminalIntentAndDeniesRetryAndReads()
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);
        using var host=new WebApplicationFactory<Program>().WithWebHostBuilder(builder=>builder.ConfigureServices(services=>services.Replace(ServiceDescriptor.Singleton<IErasureLedger>(new UnavailableLedger()))));
        using var client=host.CreateClient();using var request=DeletePermanent(s,"/api/records/"+target.RecordId,Bytes(new{expectedRevision=1}));using var response=await client.SendAsync(request);await Failure(response,503,"persistence_unavailable");
        await using var db=fixture.Context();Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="record" && x.ResourceId==target.RecordId));Assert.True(await db.Records.AnyAsync(x=>x.PublicId==target.RecordId));Assert.Null((await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="record-purge/"+target.RecordId)).CompletedAt);
        using(var retry=await Permanent(s,target.RecordId))await Failure(retry,404,"not_found");using var get=await Read(s,"/api/records/"+target.RecordId);await Failure(get,404,"not_found");
    }
    private static string Hash(string access){Assert.True(OpaqueAccessHandle.TryHash(access,out var verifier));return verifier;}
'''
Path('/tmp/cerberus-uc22-http-tests.cs').write_text(header+owned+generic+common+helpers+'}\n')
