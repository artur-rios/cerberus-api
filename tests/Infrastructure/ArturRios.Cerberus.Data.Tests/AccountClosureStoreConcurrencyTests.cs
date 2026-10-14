using System.Data.Common;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountClosureStoreTests
{
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenConcurrentClosures_WhenRootsOverlap_ThenCompleteWithoutDeadlockOrDeadlineExtension(bool reciprocal)
    {
        using var oneKey=new ProtectionFixture();using var twoKey=new ProtectionFixture();
        var one=await ProfileSetup.Create(fixture,oneKey);var two=reciprocal?await ProfileSetup.Create(fixture,twoKey):one;
        if(reciprocal)
        {
            var a=await AssociationSetup.Items(fixture,one);var b=await AssociationSetup.Items(fixture,two);
            await AssociationSetup.Grant(fixture,one,oneKey,two,twoKey,a.Collection);
            await AssociationSetup.Grant(fixture,two,twoKey,one,oneKey,b.Collection);
        }
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var count=0;
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var hook=new ClosureBefore(c=>c.CommandText.Contains("FROM cerberus.account")&&c.CommandText.Contains("FOR UPDATE"),async _=>{
            if(Interlocked.Increment(ref count)==2)ready.TrySetResult();await release.Task.WaitAsync(ct.Token);});
        var store=new AccountClosureStore(new ProfileSetup.Factory(fixture,hook));
        var first=store.RequestAsync(Request(one),ct.Token);var second=store.RequestAsync(Request(two),ct.Token);
        try{await ready.Task.WaitAsync(ct.Token);}finally{release.TrySetResult();}
        var result=await Task.WhenAll(first,second);Assert.All(result,x=>Assert.Null(x.Error));
        if(!reciprocal){Assert.Single(result,x=>x.Details!.AlreadyPending);Assert.Equal(result[0].Details!.ClosureDeadline,result[1].Details!.ClosureDeadline);}
        await using var db=fixture.CreateContext();Assert.Equal(AccountState.ClosurePending,await db.Accounts.Where(x=>x.Id==one.InternalId).Select(x=>x.State).SingleAsync());
        Assert.Equal(AccountState.ClosurePending,await db.Accounts.Where(x=>x.Id==two.InternalId).Select(x=>x.State).SingleAsync());
    }
    [FunctionalTheory][InlineData("revision")][InlineData("freshness")][InlineData("cancel")]
    public async Task GivenActualRootLockWait_WhenAuthorityExpiresChangesOrCallerCancels_ThenNeverClose(string change)
    {
        using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,key);
        await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();
        await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hook=new ClosureBefore(c=>c.CommandText.Contains("FROM cerberus.account")&&c.CommandText.Contains("FOR UPDATE"),_=>{ready.TrySetResult();return Task.CompletedTask;});
        var request=Request(s);if(change=="freshness")request=request with {AuthenticationIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-59};
        var pending=new AccountClosureStore(new ProfileSetup.Factory(fixture,hook)).RequestAsync(request,ct.Token);
        try
        {
            await ready.Task.WaitAsync(ct.Token);Assert.False(pending.IsCompleted);
            if(change=="revision")await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET revision=2 WHERE id={s.InternalId}");
            if(change=="freshness")await Task.Delay(TimeSpan.FromSeconds(2),ct.Token);
            if(change=="cancel")ct.Cancel();
        }
        finally{await tx.CommitAsync();}
        if(change=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        else Assert.Equal(change=="revision"?"revision_conflict":"fresh_authentication_required",(await pending).Error);
        await using var db=fixture.CreateContext();Assert.Equal(AccountState.Active,await db.Accounts.Where(x=>x.Id==s.InternalId).Select(x=>x.State).SingleAsync());
        Assert.False(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="account-purge/"+s.AccountId));
    }

    [FunctionalFact]
    public async Task GivenNativeRecipientWriterHoldingSharedLocks_WhenClosingOwner_ThenWaitAndRetainItsCommittedCiphertextWithoutDeadlock()
    {
        using var key=new ProtectionFixture();using var recipientKey=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,key);
        var recipient=await ProfileSetup.Create(fixture,recipientKey);var items=await AssociationSetup.Items(fixture,s);
        var grant=await AssociationSetup.Grant(fixture,s,key,recipient,recipientKey,items.Collection);
        await using(var db=fixture.CreateContext()){
            await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Access,ArturRios.Cerberus.Domain.Resources.CollectionGrantAccess.ReadWrite));
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES({s.InternalId},{items.Collection.Id},{items.Record.Id})");
        }
        // The native recipient wrapper binds access; regenerate it for writable authority.
        var wrapper=key.WrapGrant(s.AccountId,items.Collection.PublicId,grant.PublicId,recipient.Actor,recipientKey.Material.RecipientKey,1,1);
        await using(var db=fixture.CreateContext())await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(wrapper,ProtectionFixture.Json)));
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var hook=new ClosureBefore(c=>c.CommandText.Contains("SELECT FROM cerberus.record")&&c.CommandText.Contains("FOR NO KEY UPDATE"),async _=>{ready.TrySetResult();await release.Task.WaitAsync(ct.Token);});
        var envelope=ProfileSetup.Envelope();var writer=new ArturRios.Cerberus.Data.Records.RecordUpdateStore(new ProfileSetup.Factory(fixture,hook))
            .UpdateAsync(new(recipient.Actor,recipient.Verifier,items.Record.PublicId,new(1,envelope,DateTimeOffset.UtcNow)),ct.Token);
        await ready.Task.WaitAsync(ct.Token);var closing=new AccountClosureStore(fixture).RequestAsync(Request(s),ct.Token);
        try{Assert.False(writer.IsCompleted);Assert.False(closing.IsCompleted);}finally{release.TrySetResult();}
        Assert.Null((await writer.WaitAsync(ct.Token)).Error);Assert.Null((await closing.WaitAsync(ct.Token)).Error);
        await using var final=fixture.CreateContext();Assert.Equal(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelope,ProtectionFixture.Json),
            await final.Records.Where(x=>x.Id==items.Record.Id).Select(x=>x.Envelope).SingleAsync());
    }
    private sealed class ClosureBefore(Func<DbCommand,bool> match,Func<DbCommand,Task> action):DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> result,CancellationToken ct=default)
        {if(match(c))await action(c);return result;}
    }
}
