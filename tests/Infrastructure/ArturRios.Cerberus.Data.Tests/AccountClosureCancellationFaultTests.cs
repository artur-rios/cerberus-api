using System.Data.Common;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountClosureCancellationStoreTests
{
    [FunctionalTheory][InlineData("state")][InlineData("ciphertext")][InlineData("session")][InlineData("grant")]
    [InlineData("target")][InlineData("work")][InlineData("terminal")][InlineData("cancel")][InlineData("timeout")]
    public async Task GivenPostWriteStorageFault_WhenCancelling_ThenRollbackWholeTransactionBeforeCleanRetry(string fault)
    {
        using var key=new ProtectionFixture();using var recipientKey=new ProtectionFixture();var s=await Setup(key);
        var items=await AssociationSetup.Items(fixture,s);var recipient=await ProfileSetup.Create(fixture,recipientKey);
        var grant=await AssociationSetup.Grant(fixture,s,key,recipient,recipientKey,items.Collection);
        await using var db=fixture.CreateContext();var software=new SoftwareGrant{AccountId=s.InternalId,PublicId=Guid.NewGuid(),HeimdallSoftwarePublicId=Guid.NewGuid(),RecipientKey=[1]};
        db.SoftwareGrants.Add(software);await db.SaveChangesAsync();await Close(s);var before=await Snapshot(db,s);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hook=new CancelAfterWrite(async c=>
        {
            if(fault=="cancel"){ct.Cancel();ct.Token.ThrowIfCancellationRequested();}
            if(fault=="timeout")throw new TimeoutException("private storage detail");
            await using var command=c.Connection!.CreateCommand();command.Transaction=c.Transaction;
            command.CommandText=fault switch {
                "state"=>"UPDATE cerberus.account SET state=1 WHERE id=@owner",
                "ciphertext"=>"UPDATE cerberus.record SET envelope='\\x01'::bytea WHERE id=@record",
                "session"=>"UPDATE cerberus.vault_access_session SET revoked=false WHERE account_id=@owner",
                "grant"=>"UPDATE cerberus.collection_grant SET state=0 WHERE id=@grant",
                "target"=>"INSERT INTO cerberus.software_grant_record(account_id,grant_id,record_id,recipient_key_envelope) VALUES(@owner,@software,@record,'\\x01'::bytea)",
                "terminal"=>"INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at) VALUES('record',@public,statement_timestamp())",
                _=>"UPDATE cerberus.retention_work_item SET completed_at=NULL WHERE operation_key=@work"};
            foreach(var p in new NpgsqlParameter[]{new("owner",s.InternalId),new("record",items.Record.Id),new("grant",grant.Id),new("software",software.Id),new("public",items.Record.PublicId),new("work","account-purge/"+s.AccountId)})command.Parameters.Add(p);
            await command.ExecuteNonQueryAsync();
        });
        var store=new AccountClosureCancellationStore(new ProfileSetup.Factory(fixture,hook));
        if(fault=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.CancelAsync(Request(s),ct.Token));
        else Assert.Equal("persistence_unavailable",(await store.CancelAsync(Request(s),ct.Token)).Error);
        Assert.Equal(before,await Snapshot(db,s));Assert.Equal(2,await db.Accounts.Where(x=>x.Id==recipient.InternalId).Select(x=>x.RevocationGeneration).SingleAsync());
        Assert.Null((await new AccountClosureCancellationStore(fixture).CancelAsync(Request(s),default)).Error);
    }
    [FunctionalTheory][InlineData("account")][InlineData("retention_work_item")]
    public async Task GivenSuppressedAccountOrJobWrite_WhenCancelling_ThenRollbackAndPermitRetry(string table)
    {
        using var key=new ProtectionFixture();var s=await Setup(key);await Close(s);await using var db=fixture.CreateContext();
        var id=table=="account"?s.InternalId:await db.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+s.AccountId).Select(x=>x.Id).SingleAsync();
        var name="uc07_suppress_"+Guid.NewGuid().ToString("N");var sql="CREATE FUNCTION cerberus."+name+"() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.id="+id+" THEN RETURN NULL; END IF; RETURN NEW; END $$; CREATE TRIGGER "+name+" BEFORE UPDATE ON cerberus."+table+" FOR EACH ROW EXECUTE FUNCTION cerberus."+name+"();";
        var before=await Snapshot(db,s);await db.Database.ExecuteSqlRawAsync(sql);
        try{Assert.Equal("persistence_unavailable",(await new AccountClosureCancellationStore(fixture).CancelAsync(Request(s),default)).Error);Assert.Equal(before,await Snapshot(db,s));}
        finally{var uninstall="DROP TRIGGER "+name+" ON cerberus."+table+"; DROP FUNCTION cerberus."+name+"();";await db.Database.ExecuteSqlRawAsync(uninstall);}
        Assert.Null((await new AccountClosureCancellationStore(fixture).CancelAsync(Request(s),default)).Error);
    }
    private sealed class CancelAfterWrite(Func<DbCommand,Task> action):DbCommandInterceptor
    {
        private bool written,used;
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData d,DbDataReader r,CancellationToken ct=default)
        {if(c.CommandText.Contains("WITH authorized AS"))written=true;return ValueTask.FromResult(r);}
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default)
        {if(written&&!used){used=true;await action(c);}return r;}
    }
}
