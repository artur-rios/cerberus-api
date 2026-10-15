using System.Data.Common;
using ArturRios.Cerberus.Data.Accounts.Export;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountExportStoreTests
{
    [FunctionalTheory][InlineData(-60,false)][InlineData(-59,true)][InlineData(0,true)][InlineData(1,false)]
    public async Task GivenExactSignedAuthenticationBoundary_WhenDatabaseAdmitsExport_ThenUseExclusiveSixtySeconds(int delta,bool allowed)
    {
        // Given: control only the real DECLARE clock expression, with no production clock hook.
        var w=await World();await using var db=fixture.CreateContext();var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync();var clock=new AdmissionClock(now);
        var request=Request(w) with{AuthenticationIssuedAt=now.ToUnixTimeSeconds()+delta};var before=await Snapshot(w);
        // When: PostgreSQL evaluates admission exactly on the stated signed-second boundary.
        var result=await new AccountExportStore(Factory(clock)).OpenAsync(request,default);
        // Then:59seconds and current second pass;60seconds/future fail. No native changes.
        Assert.True(clock.Used);if(allowed){Assert.Null(result.Error);await using var lease=Assert.IsAssignableFrom<IAccountExportLease>(result.Lease);var count=0;await foreach(var entry in lease.ReadAsync(default))count++;Assert.True(count>=6);}
        else{Assert.Equal("fresh_authentication_required",result.Error);Assert.Null(result.Lease);}Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalFact]
    public async Task GivenOneExportReader_WhenAnotherConsumerStarts_ThenDenyReplayAndKeepOriginalReaderValid()
    {
        // Given: one real reader already owns an admitted cursor.
        var w=await World();await using var lease=await Open(w);await using var first=lease.ReadAsync(default).GetAsyncEnumerator();Assert.True(await first.MoveNextAsync());
        // When/Then: a second consumer cannot duplicate bytes or dispose the active owner's stream.
        await using(var second=lease.ReadAsync(default).GetAsyncEnumerator())await Assert.ThrowsAsync<AccountExportException>(()=>second.MoveNextAsync().AsTask());
        var count=1;while(await first.MoveNextAsync())count++;Assert.True(count>=6);
        await using var afterEof=lease.ReadAsync(default).GetAsyncEnumerator();await Assert.ThrowsAsync<AccountExportException>(()=>afterEof.MoveNextAsync().AsTask());
    }
    [FunctionalTheory][InlineData("revision","revision_conflict")][InlineData("revoked","vault_access_denied")][InlineData("closing","not_found")]
    public async Task GivenWinnerBeforeCursorCapture_WhenExporting_ThenObserveCommittedAuthorityAndPreserveWinner(string change,string error)
    {
        // Given: pause the actual DECLARE before PostgreSQL captures its snapshot.
        var w=await World();var gate=new DeclareGate();var pending=new AccountExportStore(Factory(gate)).OpenAsync(Request(w),default);
        try
        {
            await RequireBoundary(gate.Reached.Task,pending);
            await ChangeAuthority(w,change);var winner=await Snapshot(w);gate.Release.TrySetResult();
            // When/Then: the committed winner determines admission; export never rewrites it.
            var result=await pending;Assert.Equal(error,result.Error);Assert.Null(result.Lease);Assert.Equal(winner,await Snapshot(w));
        }
        finally{gate.Release.TrySetResult();var result=await pending;if(result.Lease is not null)await result.Lease.DisposeAsync();}
    }

    [FunctionalTheory][InlineData("revision","revision_conflict")][InlineData("revoked","vault_access_denied")]
    [InlineData("generation","vault_access_denied")][InlineData("closing","not_found")][InlineData("terminal","not_found")]
    [InlineData("generationWithSession","vault_access_denied")][InlineData("policyWithSession","vault_access_denied")]
    public async Task GivenAuthorityChangesAfterFirstBatch_WhenReadingNextBatch_ThenAbortWithCurrentDecisionAndPreserveWinner(string change,string error)
    {
        // Given: the cursor captured more than three batches before authority changes.
        var w=await World(96);await using var lease=await Open(w);await using var reader=lease.ReadAsync(default).GetAsyncEnumerator();
        for(var i=0;i<32;i++)Assert.True(await reader.MoveNextAsync());
        await ChangeAuthority(w,change);var winner=await Snapshot(w);
        // When/Then: a separate READ COMMITTED fence sees the change despite the old cursor snapshot.
        var failure=await Assert.ThrowsAsync<AccountExportException>(async()=>{while(await reader.MoveNextAsync()){};});
        Assert.Equal(error,failure.Error);Assert.Equal(winner,await Snapshot(w));
    }

    [FunctionalFact]
    public async Task GivenPendingRecordBecomesTerminal_WhenReadingLaterBatches_ThenNeverReleaseItsOldSnapshotBytes()
    {
        // Given: select a terminal target that has not been delivered in the first batch.
        var w=await World(96);await using var lease=await Open(w);await using var reader=lease.ReadAsync(default).GetAsyncEnumerator();var seen=new HashSet<Guid>();
        for(var i=0;i<32;i++){Assert.True(await reader.MoveNextAsync());if(reader.Current is AccountExportRecord r)seen.Add(r.ResourceId);}
        await using var db=fixture.CreateContext();var target=await db.Records.Where(x=>x.AccountId==w.Owner.InternalId&&!seen.Contains(x.PublicId)).Select(x=>x.PublicId).FirstAsync();
        db.TerminalErasures.Add(new(){ResourceKind="record",ResourceId=target,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();var winner=await Snapshot(w);
        // When/Then: old cursor contents cannot bypass fresh permanent intent.
        await Assert.ThrowsAsync<AccountExportException>(async()=>{while(await reader.MoveNextAsync()){if(reader.Current is AccountExportRecord r)Assert.NotEqual(target,r.ResourceId);}});
        Assert.Equal(winner,await Snapshot(w));
    }

    [FunctionalFact]
    public async Task GivenNativeWriterAfterCapture_WhenItTakesOwnerWriteGate_ThenCommitWithoutBlockingAndExportOriginalSnapshot()
    {
        // Given: a pending record from the original captured view.
        var w=await World(96);await using var lease=await Open(w);await using var reader=lease.ReadAsync(default).GetAsyncEnumerator();var seen=new HashSet<Guid>();
        for(var i=0;i<32;i++){Assert.True(await reader.MoveNextAsync());if(reader.Current is AccountExportRecord r)seen.Add(r.ResourceId);}
        await using var db=fixture.CreateContext();var target=await db.Records.AsNoTracking().FirstAsync(x=>x.AccountId==w.Owner.InternalId&&!seen.Contains(x.PublicId));byte[] replacement=[17,19,23];
        // When: use the real canonical owner row gate as well as the native update.
        async Task Write(){await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM cerberus.account WHERE id={w.Owner.InternalId} FOR NO KEY UPDATE");await db.Records.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,replacement).SetProperty(r=>r.Revision,2));await tx.CommitAsync();}
        await Write().WaitAsync(TimeSpan.FromSeconds(10));var winner=await Snapshot(w);AccountExportRecord? archived=null;
        while(await reader.MoveNextAsync())if(reader.Current is AccountExportRecord r&&r.ResourceId==target.PublicId)archived=r;
        // Then: no writer lock was retained, and newer ciphertext is never mixed into the package.
        Assert.NotNull(archived);Assert.Equal(1,archived.Revision);Assert.Equal(target.Envelope,archived.Envelope);Assert.Equal(winner,await Snapshot(w));
    }

    [FunctionalFact]
    public async Task GivenRetainedDeadlineAfterInitialFetch_WhenLaterFetchCrossesIt_ThenUseCapturedEligibilityTime()
    {
        // Given: arm the short deadline at DECLARE, after all setup, using actual database time.
        var w=await World(96);var gate=new DeclareGate();var pending=new AccountExportStore(Factory(gate)).OpenAsync(Request(w),default);DateTimeOffset deadline;
        try
        {
            await RequireBoundary(gate.Reached.Task,pending);await using var db=fixture.CreateContext();
            deadline=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp()+interval '3 seconds' AS \"Value\"").SingleAsync();
            await db.Records.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,deadline.AddHours(-720)).SetProperty(r=>r.PurgeAt,deadline));gate.Release.TrySetResult();
        }
        finally{gate.Release.TrySetResult();}
        var opened=await pending;Assert.Null(opened.Error);await using var lease=Assert.IsAssignableFrom<IAccountExportLease>(opened.Lease);await using var reader=lease.ReadAsync(default).GetAsyncEnumerator();var total=0;
        for(var i=0;i<32;i++){Assert.True(await reader.MoveNextAsync());if(reader.Current is AccountExportRecord)total++;}
        // When: later FETCH statements run strictly after the original deadline.
        var delay=deadline-DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(100);if(delay>TimeSpan.Zero)await Task.Delay(delay);
        while(await reader.MoveNextAsync())if(reader.Current is AccountExportRecord)total++;
        // Then: this package has all97 records; a newly captured package omits them.
        Assert.Equal(97,total);var next=await Capture(w);Assert.Empty(next.Entries.OfType<AccountExportRecord>());
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenOpenCursor_WhenDisposedEarly_ThenReleaseActualBackendTransaction(bool enumerate)
    {
        // Given/When: release either an unused lease or a reader abandoned after one row.
        var w=await World(96);var tag="uc54_early_"+Guid.NewGuid().ToString("N");var before=await Snapshot(w);var lease=await Open(w,Factory(tag:tag));Assert.Equal(1,await ActiveTransactions(tag));
        if(enumerate){await using(var reader=lease.ReadAsync(default).GetAsyncEnumerator())Assert.True(await reader.MoveNextAsync());Assert.Equal(0,await ActiveTransactions(tag));}
        await lease.DisposeAsync();
        // Then: backend resources are gone and vault/auth state is unchanged.
        Assert.Equal(0,await ActiveTransactions(tag));Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalFact]
    public async Task GivenCancellationDuringLaterFetch_WhenReading_ThenPropagateAndReleaseActualBackendTransaction()
    {
        // Given: an actual later FETCH is pending on the caller's cancellation token.
        var w=await World(96);var tag="uc54_cancel_"+Guid.NewGuid().ToString("N");var gate=new FetchGate();var before=await Snapshot(w);await using var lease=await Open(w,Factory(gate,tag));using var ct=new CancellationTokenSource();await using var reader=lease.ReadAsync(ct.Token).GetAsyncEnumerator();
        for(var i=0;i<32;i++)Assert.True(await reader.MoveNextAsync());var pending=reader.MoveNextAsync().AsTask();await RequireBoundary(gate.Reached.Task,pending);
        // When/Then: cancellation tears down transaction/connection without a success EOF.
        ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);Assert.Equal(0,await ActiveTransactions(tag));Assert.Equal(before,await Snapshot(w));
    }

    private async Task ChangeAuthority(Graph w,string change)
    {
        await using var db=fixture.CreateContext();switch(change)
        {
            case "revision":await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.Revision,2));break;
            case "generation":await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.RevocationGeneration,2));break;
            case "generationWithSession":
                await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.RevocationGeneration,2));
                await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.RevocationGeneration,2));break;
            case "policyWithSession":
                await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.PolicyRevision,2));
                await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.PolicyRevision,2));break;
            case "closing":await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));break;
            case "revoked":await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.Revoked,true));break;
            case "terminal":db.TerminalErasures.Add(new(){ResourceKind="account",ResourceId=w.Owner.AccountId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();break;
        }
    }
    private static async Task RequireBoundary(Task boundary,Task operation)
    {var winner=await Task.WhenAny(boundary,operation).WaitAsync(TimeSpan.FromSeconds(20));Assert.True(winner==boundary,"Export completed without reaching the required real SQL boundary.");await boundary;}
    private sealed class DeclareGate:DbCommandInterceptor
    {
        public TaskCompletionSource Reached{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default)
        {if(c.CommandText.TrimStart().StartsWith("DECLARE ",StringComparison.OrdinalIgnoreCase)){Reached.TrySetResult();await Release.Task.WaitAsync(ct);}return r;}
    }
    private sealed class AdmissionClock(DateTimeOffset now):DbCommandInterceptor
    {
        public bool Used{get;private set;}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default)
        {
            if(c.CommandText.TrimStart().StartsWith("DECLARE ",StringComparison.OrdinalIgnoreCase))
            {Assert.Contains("statement_timestamp()",c.CommandText);c.CommandText=c.CommandText.Replace("statement_timestamp()","@export_test_clock",StringComparison.Ordinal);c.Parameters.Add(new NpgsqlParameter("export_test_clock",now));Used=true;}
            return ValueTask.FromResult(r);
        }
    }
    private sealed class FetchGate:DbCommandInterceptor
    {
        private int calls;
        public TaskCompletionSource Reached{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default)
        {if(c.CommandText.TrimStart().StartsWith("FETCH ",StringComparison.OrdinalIgnoreCase)&&++calls==2){Reached.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}return r;}
    }
}
