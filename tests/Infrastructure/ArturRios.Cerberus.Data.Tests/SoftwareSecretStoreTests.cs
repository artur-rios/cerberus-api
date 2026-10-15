using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data;
using ArturRios.Cerberus.Data.Software;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ArturRios.Cerberus.Domain.Software;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class SoftwareSecretStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("record",false)][InlineData("collection",false)][InlineData("record",true)]
    public async Task GivenCurrentNativeSoftwareGrant_WhenReading_ThenReturnExactCiphertextWithoutMutation(string kind,bool member)
    {
        using var s=await SoftwareGrantSetup.Create(fixture,true);var grant=s.Request();
        if(member)grant=grant with{Input=grant.Input with{Targets=grant.Input.Targets.Where(x=>x.Kind=="collection").ToArray()}};
        Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(grant,default)).Error);
        if(member)await Sql("INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES({0},{1},{2})",s.Owner.InternalId,s.Items.Collection.Id,s.Items.Record.Id);
        var request=Request(s,kind,member);var before=await SoftwareGrantSetup.Vault(fixture,s.Owner.InternalId);var aggregate=await SoftwareGrantSetup.Aggregate(fixture,s.Grant);
        var result=await new SoftwareSecretStore(fixture).ReadAsync(request,default);
        Assert.Null(result.Error);Assert.NotNull(result.Data);Assert.True(result.Data.IsValid(request.Identity,request.Input));
        Assert.Equal(s.Owner.AccountId,result.Data.OwnerAccountId);Assert.Equal(kind=="record"?s.Items.Record.PublicId:s.Items.Collection.PublicId,result.Data.Target.Id);
        Assert.Equal(kind=="record"?s.Items.Record.Envelope:s.Items.Collection.Envelope,SoftwareGrantSetup.Bytes(result.Data.Target.Envelope));
        Assert.Equal(grant.Input.Targets.Single(x=>x.Kind==(member?"collection":kind)).KeyEnvelope,result.Data.Provisioning.KeyEnvelope);
        Assert.Equal(before,await SoftwareGrantSetup.Vault(fixture,s.Owner.InternalId));Assert.Equal(aggregate,await SoftwareGrantSetup.Aggregate(fixture,s.Grant));
    }
    [FunctionalTheory]
    [InlineData("application","not_found")][InlineData("grant","not_found")][InlineData("target","not_found")]
    [InlineData("revoked","not_found")][InlineData("account","not_found")][InlineData("accountTerminal","not_found")]
    [InlineData("recordTrash","not_found")][InlineData("recordTerminal","not_found")][InlineData("noWrap","not_found")]
    [InlineData("ancestorTrash","not_found")][InlineData("ancestorTerminal","not_found")][InlineData("cycle","persistence_unavailable")]
    [InlineData("recordEnvelope","persistence_unavailable")][InlineData("recordRevision","persistence_unavailable")][InlineData("recordSequence","persistence_unavailable")]
    [InlineData("recordPurge","persistence_unavailable")][InlineData("wrap","persistence_unavailable")][InlineData("recipient","persistence_unavailable")]
    [InlineData("grantRevision","persistence_unavailable")][InlineData("pins","persistence_unavailable")][InlineData("pinsMissing","persistence_unavailable")]
    [InlineData("pinsEpoch","persistence_unavailable")][InlineData("expiry","authentication_required")]
    public async Task GivenHiddenOrCorruptRequiredAuthority_WhenReading_ThenFailWithoutMutationOrPartialCiphertext(string mode,string error)
    {
        using var s=await SoftwareGrantSetup.Create(fixture);Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(s.Request(),default)).Error);var request=Request(s);
        if(mode=="application")request=request with{Identity=request.Identity with{ApplicationId=Guid.NewGuid()}};
        else if(mode=="grant")request=request with{Input=request.Input with{GrantId=Guid.NewGuid()}};
        else if(mode=="target")request=request with{Input=request.Input with{Id=Guid.NewGuid()}};
        else if(mode=="expiry")request=request with{Identity=request.Identity with{ExpiresAt=DateTimeOffset.FromUnixTimeSeconds(1)}};
        else if(mode is "accountTerminal" or "recordTerminal" or "ancestorTerminal")
        {
            if(mode=="ancestorTerminal")await Sql("UPDATE cerberus.record SET folder_id={0} WHERE id={1}",s.Items.Folder.Id,s.Items.Record.Id);
            await using var db=fixture.CreateContext();db.TerminalErasures.Add(new(){ResourceKind=mode=="accountTerminal"?"account":mode=="recordTerminal"?"record":"folder",ResourceId=mode=="accountTerminal"?s.Owner.AccountId:mode=="recordTerminal"?s.Items.Record.PublicId:s.Items.Folder.PublicId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();
        }
        else if(mode is "ancestorTrash" or "cycle")
        {
            await Sql("UPDATE cerberus.record SET folder_id={0} WHERE id={1}",s.Items.Folder.Id,s.Items.Record.Id);
            await Sql(mode=="cycle"?"UPDATE cerberus.folder SET parent_folder_id=id WHERE id={0}":"UPDATE cerberus.folder SET deleted_at=statement_timestamp() WHERE id={0}",s.Items.Folder.Id);
        }
        else await Sql(mode switch
        {
            "revoked"=>"UPDATE cerberus.software_grant SET state=1 WHERE account_id={0}","account"=>"UPDATE cerberus.account SET state=1 WHERE id={0}",
            "recordTrash"=>"UPDATE cerberus.record SET deleted_at=statement_timestamp() WHERE account_id={0}",
            "noWrap"=>"DELETE FROM cerberus.software_grant_record WHERE account_id={0}",
            "recordEnvelope"=>"UPDATE cerberus.record SET envelope='bad'::bytea WHERE account_id={0}",
            "recordRevision"=>"UPDATE cerberus.record SET revision=0 WHERE account_id={0}","recordSequence"=>"UPDATE cerberus.record SET server_sequence=0 WHERE account_id={0}",
            "recordPurge"=>"UPDATE cerberus.record SET purge_at=statement_timestamp() WHERE account_id={0}",
            "wrap"=>"UPDATE cerberus.software_grant_record SET recipient_key_envelope='bad'::bytea WHERE account_id={0}",
            "recipient"=>"UPDATE cerberus.software_grant SET recipient_key='bad'::bytea WHERE account_id={0}",
            "grantRevision"=>"UPDATE cerberus.software_grant SET revision=2 WHERE account_id={0}",
            "pinsMissing"=>"DELETE FROM cerberus.vault_protection WHERE account_id={0}","pinsEpoch"=>"UPDATE cerberus.vault_protection SET key_epoch=2 WHERE account_id={0}",
            _=>"UPDATE cerberus.vault_protection SET material='bad'::bytea WHERE account_id={0}"
        },s.Owner.InternalId);
        await Failure(s,request,error);
    }
    [FunctionalTheory]
    [InlineData("direct",true)][InlineData("folder",true)][InlineData("removed",false)][InlineData("collectionTrash",false)]
    [InlineData("collectionTerminal",false)][InlineData("folderTrash",false)][InlineData("noCollectionWrap",false)]
    public async Task GivenCollectionProvisioning_WhenReadingMember_ThenRequireExactCurrentVisibleMembership(string mode,bool success)
    {
        using var s=await SoftwareGrantSetup.Create(fixture);var grant=s.Request();grant=grant with{Input=grant.Input with{Targets=grant.Input.Targets.Where(x=>x.Kind=="collection").ToArray()}};
        Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(grant,default)).Error);
        if(mode is "folder" or "folderTrash")
        {
            await Sql("UPDATE cerberus.record SET folder_id={0} WHERE id={1}",s.Items.Folder.Id,s.Items.Record.Id);
            await Sql("INSERT INTO cerberus.collection_folder(account_id,collection_id,folder_id) VALUES({0},{1},{2})",s.Owner.InternalId,s.Items.Collection.Id,s.Items.Folder.Id);
            if(mode=="folderTrash")await Sql("UPDATE cerberus.folder SET deleted_at=statement_timestamp() WHERE id={0}",s.Items.Folder.Id);
        }
        else if(mode!="removed")await Sql("INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES({0},{1},{2})",s.Owner.InternalId,s.Items.Collection.Id,s.Items.Record.Id);
        if(mode=="collectionTrash")await Sql("UPDATE cerberus.collection SET deleted_at=statement_timestamp() WHERE id={0}",s.Items.Collection.Id);
        if(mode=="collectionTerminal"){await using var db=fixture.CreateContext();db.TerminalErasures.Add(new(){ResourceKind="collection",ResourceId=s.Items.Collection.PublicId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        if(mode=="noCollectionWrap")await Sql("DELETE FROM cerberus.software_grant_collection WHERE account_id={0}",s.Owner.InternalId);
        var request=Request(s,member:true);if(!success){await Failure(s,request,"not_found");return;}
        var result=await new SoftwareSecretStore(fixture).ReadAsync(request,default);Assert.Null(result.Error);Assert.True(result.Data!.IsValid(request.Identity,request.Input));
        await Failure(s,request with{Input=request.Input with{CollectionId=null}},"not_found");
        await Failure(s,request with{Input=request.Input with{CollectionId=Guid.NewGuid()}},"not_found");
    }
    [FunctionalTheory][InlineData("envelope")][InlineData("epoch")][InlineData("wrap")][InlineData("revision")][InlineData("sequence")][InlineData("purge")]
    public async Task GivenCorruptCollectionProvisioning_WhenReadingMember_ThenNeverReleaseRecord(string mode)
    {
        using var s=await SoftwareGrantSetup.Create(fixture);Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(s.Request(),default)).Error);
        await Sql("INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES({0},{1},{2})",s.Owner.InternalId,s.Items.Collection.Id,s.Items.Record.Id);
        await Sql(mode switch{"envelope"=>"UPDATE cerberus.collection SET envelope='bad'::bytea WHERE account_id={0}","epoch"=>"UPDATE cerberus.collection SET key_epoch=2 WHERE account_id={0}","wrap"=>"UPDATE cerberus.software_grant_collection SET recipient_key_envelope='bad'::bytea WHERE account_id={0}","revision"=>"UPDATE cerberus.collection SET revision=0 WHERE account_id={0}","sequence"=>"UPDATE cerberus.collection SET server_sequence=0 WHERE account_id={0}",_=>"UPDATE cerberus.collection SET purge_at=statement_timestamp() WHERE account_id={0}"},s.Owner.InternalId);
        await Failure(s,Request(s,member:true),"persistence_unavailable");
    }
    [FunctionalTheory][InlineData("cancel")][InlineData("database")][InlineData("timeout")][InlineData("wrapped")][InlineData("json")][InlineData("format")][InlineData("update")]
    public async Task GivenStorageFailure_WhenReading_ThenReturnOnlySafeUnavailable(string kind)
    {
        using var s=await SoftwareGrantSetup.Create(fixture);
        Exception e=kind switch{"cancel"=>new OperationCanceledException("private"),"database"=>new NpgsqlException("private"),"timeout"=>new TimeoutException("private"),"wrapped"=>new InvalidOperationException("private",new NpgsqlException("private")),"json"=>new JsonException("private"),"format"=>new FormatException("private"),_=>new DbUpdateException("private")};
        var result=await new SoftwareSecretStore(new ProfileSetup.Factory(fixture,new Broken(e))).ReadAsync(Request(s),default);Assert.Equal("persistence_unavailable",result.Error);Assert.Null(result.Data);
    }
    [FunctionalFact]
    public async Task GivenCancelledCaller_WhenReading_ThenPropagateBeforeStorage()
    {
        using var s=await SoftwareGrantSetup.Create(fixture);using var ct=new CancellationTokenSource();ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new SoftwareSecretStore(fixture).ReadAsync(Request(s),ct.Token));
    }
    [FunctionalTheory][InlineData("owner")][InlineData("kind")][InlineData("id")][InlineData("epoch")][InlineData("grant")][InlineData("revision")][InlineData("application")][InlineData("signature")]
    public async Task GivenWellFormedButWrongNativeBinding_WhenReading_ThenRejectCiphertext(string mode)
    {
        using var s=await SoftwareGrantSetup.Create(fixture);Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(s.Request(),default)).Error);
        var wrap=s.Target("record",s.Items.Record.PublicId,owner:mode=="owner"?Guid.NewGuid():null,signedKind:mode=="kind"?"collection":null,signedId:mode=="id"?Guid.NewGuid():null,epoch:mode=="epoch"?2:1,grant:mode=="grant"?Guid.NewGuid():null,expected:mode=="revision"?1:null,identity:mode=="application"?Guid.NewGuid():null).KeyEnvelope;
        if(mode=="signature")wrap=wrap with{Signature=ArturRios.Cerberus.TestSupport.ProtectionFixture.Encode(new byte[64])};
        await Sql("UPDATE cerberus.software_grant_record SET recipient_key_envelope={0} WHERE account_id={1}",SoftwareGrantSetup.Bytes(wrap),s.Owner.InternalId);
        await Failure(s,Request(s),"persistence_unavailable");
    }
    [FunctionalTheory][InlineData("revocation","not_found")][InlineData("closure","not_found")][InlineData("membership","not_found")][InlineData("expiry","authentication_required")][InlineData("rotation","persistence_unavailable")]
    public async Task GivenAuthorityChangesWhileWaitingForOwnerLock_WhenReading_ThenRecheckAfterWait(string mode,string error)
    {
        using var s=await SoftwareGrantSetup.Create(fixture);Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(s.Request(),default)).Error);
        await Sql("INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES({0},{1},{2})",s.Owner.InternalId,s.Items.Collection.Id,s.Items.Record.Id);
        var request=Request(s,member:mode=="membership");
        if(mode=="expiry")request=request with{Identity=request.Identity with{ExpiresAt=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeSeconds())}};
        await using var writer=fixture.CreateContext();await using var tx=await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.Owner.InternalId} FOR UPDATE");
        var probe=new OwnerWait();var read=new SoftwareSecretStore(new ProfileSetup.Factory(fixture,probe)).ReadAsync(request,default);
        await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if(mode=="expiry")await writer.Database.ExecuteSqlRawAsync("SELECT pg_sleep(3.1)");
        else await writer.Database.ExecuteSqlRawAsync(mode switch{"revocation"=>"UPDATE cerberus.software_grant SET state=1 WHERE account_id={0}","closure"=>"UPDATE cerberus.account SET state=1 WHERE id={0}","membership"=>"DELETE FROM cerberus.collection_record WHERE account_id={0}",_=>"UPDATE cerberus.software_grant SET revision=2 WHERE account_id={0}"},s.Owner.InternalId);
        await tx.CommitAsync();var result=await read.WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(error,result.Error);Assert.Null(result.Data);
    }
    [FunctionalTheory][InlineData("record")][InlineData("wrap")][InlineData("pins")][InlineData("membership")][InlineData("grant")]
    public async Task GivenRequiredStateChangesBeforeFinalRead_WhenReading_ThenNeverReturnStaleCiphertext(string kind)
    {
        using var s=await SoftwareGrantSetup.Create(fixture);Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(s.Request(),default)).Error);
        await Sql("INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES({0},{1},{2})",s.Owner.InternalId,s.Items.Collection.Id,s.Items.Record.Id);
        var before=await SoftwareGrantSetup.Vault(fixture,s.Owner.InternalId);var aggregate=await SoftwareGrantSetup.Aggregate(fixture,s.Grant);
        var result=await new SoftwareSecretStore(new ProfileSetup.Factory(fixture,new FinalFault(kind,s.Owner.InternalId))).ReadAsync(Request(s,member:true),default);
        Assert.Equal(kind is "membership" or "grant"?"not_found":"persistence_unavailable",result.Error);Assert.Null(result.Data);
        Assert.Equal(before,await SoftwareGrantSetup.Vault(fixture,s.Owner.InternalId));Assert.Equal(aggregate,await SoftwareGrantSetup.Aggregate(fixture,s.Grant));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenMissingRequiredAncestor_WhenReading_ThenHideDirectAndMemberRecord(bool member)
    {
        using var s=await SoftwareGrantSetup.Create(fixture);Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(s.Request(),default)).Error);
        await Sql("INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES({0},{1},{2})",s.Owner.InternalId,s.Items.Collection.Id,s.Items.Record.Id);
        var result=await new SoftwareSecretStore(new ProfileSetup.Factory(fixture,new MissingAncestor())).ReadAsync(Request(s,member:member),default);
        Assert.Equal("not_found",result.Error);Assert.Null(result.Data);
    }
    private sealed class MissingAncestor:DbCommandInterceptor
    {
        private int reads;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            if(c.CommandText.Contains("WITH RECURSIVE grant_row")&&++reads==2)
            {
                await using var fault=c.Connection!.CreateCommand();fault.Transaction=c.Transaction;
                fault.CommandText="SET LOCAL session_replication_role=replica; UPDATE cerberus.record SET folder_id=9223372036854775807 WHERE public_id=@id; SET LOCAL session_replication_role=origin";
                fault.Parameters.Add(new NpgsqlParameter("id",c.Parameters["id"].Value));await fault.ExecuteNonQueryAsync(ct);
            }
            return result;
        }
    }
    [FunctionalFact]
    public async Task GivenUnrelatedMalformedForeignStateAndTypedTerminalCollision_WhenReading_ThenReturnOnlyExactOwnedRecord()
    {
        using var s=await SoftwareGrantSetup.Create(fixture,true);using var other=await SoftwareGrantSetup.Create(fixture);
        Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(s.Request(),default)).Error);Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(other.Request(),default)).Error);
        await Sql("UPDATE cerberus.vault_protection SET material='bad'::bytea WHERE account_id={0}",other.Owner.InternalId);
        await Sql("UPDATE cerberus.software_grant SET recipient_key='bad'::bytea WHERE account_id={0}",other.Owner.InternalId);
        await Sql("UPDATE cerberus.record SET envelope='bad'::bytea WHERE account_id={0}",other.Owner.InternalId);
        await using(var db=fixture.CreateContext()){db.TerminalErasures.Add(new(){ResourceKind="collection",ResourceId=s.Items.Collection.PublicId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var request=Request(s);var result=await new SoftwareSecretStore(fixture).ReadAsync(request,default);Assert.Null(result.Error);Assert.True(result.Data!.IsValid(request.Identity,request.Input));
        await Failure(s,Request(s,"collection"),"not_found");
        await Failure(s,request with{Input=request.Input with{Id=other.Items.Record.PublicId}},"not_found");
    }
    [FunctionalTheory][InlineData("author")][InlineData("recipient")][InlineData("recordEpoch")]
    public async Task GivenDifferentValidCurrentCryptographicMaterial_WhenReading_ThenRequireOriginalNativeBinding(string mode)
    {
        using var s=await SoftwareGrantSetup.Create(fixture);using var changed=System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(s.Request(),default)).Error);
        var key=ArturRios.Cerberus.TestSupport.ProtectionFixture.Public(changed);
        if(mode=="author")await Sql("UPDATE cerberus.vault_protection SET material={0} WHERE account_id={1}",SoftwareGrantSetup.Bytes(s.Protection.Material with{AuthorKey=key}),s.Owner.InternalId);
        else if(mode=="recipient")await Sql("UPDATE cerberus.software_grant SET recipient_key={0} WHERE account_id={1}",SoftwareGrantSetup.Bytes(new SoftwareRecipientKey(key.Crv,key.Kty,key.X,key.Y)),s.Owner.InternalId);
        else
        {
            var envelope=JsonSerializer.Deserialize<ArturRios.Cerberus.Domain.Accounts.EncryptedEnvelope>(s.Items.Record.Envelope,ArturRios.Cerberus.TestSupport.ProtectionFixture.Json)!;
            await Sql("UPDATE cerberus.record SET envelope={0} WHERE id={1}",SoftwareGrantSetup.Bytes(envelope with{KeyEpoch=2}),s.Items.Record.Id);
        }
        await Failure(s,Request(s),"persistence_unavailable");
    }
    private sealed class OwnerWait:DbCommandInterceptor
    {
        internal TaskCompletionSource Reached{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> result,CancellationToken ct=default)
        {if(c.CommandText.Contains("SELECT FROM cerberus.account")&&c.CommandText.Contains("FOR UPDATE"))Reached.TrySetResult();return ValueTask.FromResult(result);}
    }
    private sealed class FinalFault(string kind,long owner):DbCommandInterceptor
    {
        private int reads;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            if(c.CommandText.Contains("WITH RECURSIVE grant_row")&&++reads==3)
            {
                await using var fault=c.Connection!.CreateCommand();fault.Transaction=c.Transaction;
                fault.CommandText=kind switch{"record"=>"UPDATE cerberus.record SET revision=revision+1 WHERE account_id=@owner","wrap"=>"UPDATE cerberus.software_grant_collection SET recipient_key_envelope='bad'::bytea WHERE account_id=@owner","pins"=>"UPDATE cerberus.vault_protection SET material='bad'::bytea WHERE account_id=@owner","membership"=>"DELETE FROM cerberus.collection_record WHERE account_id=@owner",_=>"UPDATE cerberus.software_grant SET state=1 WHERE account_id=@owner"};
                fault.Parameters.Add(new NpgsqlParameter("owner",owner));await fault.ExecuteNonQueryAsync(ct);
            }
            return result;
        }
    }
    private async Task Failure(SoftwareGrantSetup s,SoftwareSecretRequest request,string error)
    {
        var before=await SoftwareGrantSetup.Vault(fixture,s.Owner.InternalId);var aggregate=await SoftwareGrantSetup.Aggregate(fixture,s.Grant);
        var result=await new SoftwareSecretStore(fixture).ReadAsync(request,default);Assert.Equal(error,result.Error);Assert.Null(result.Data);
        Assert.Equal(before,await SoftwareGrantSetup.Vault(fixture,s.Owner.InternalId));Assert.Equal(aggregate,await SoftwareGrantSetup.Aggregate(fixture,s.Grant));
    }
    private sealed class Broken(Exception failure):DbCommandInterceptor
    {public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> result,CancellationToken ct=default)=>throw failure;}
    private static SoftwareSecretRequest Request(SoftwareGrantSetup s,string kind="record",bool member=false)=>new(new(s.Identity,Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds())),new(s.Grant,kind,kind=="record"?s.Items.Record.PublicId:s.Items.Collection.PublicId,member?s.Items.Collection.PublicId:null));
    private async Task Sql(string sql,params object[] args){await using var db=fixture.CreateContext();await db.Database.ExecuteSqlRawAsync(sql,args);}
}
