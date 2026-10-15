using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Data;
using ArturRios.Cerberus.Data.Accounts.Export;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ArturRios.Cerberus.WebApi.Tests;

public partial class AccountExportHttpTests
{
    [FunctionalFact]
    public async Task GivenUnavailableExportDatabase_WhenOpeningThroughHost_Then503WithoutPartialArchiveOrHealthyMutation()
    {
        var s=await SetupExport();var before=await ExportSnapshot(s);using var host=ExportWireHost(unavailable:true);using var client=host.CreateClient();using var request=ExportRequest(s);using var response=await client.SendAsync(request);await ExportFailure(response,503,"persistence_unavailable");Assert.DoesNotContain("connection",await response.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);Assert.Equal(before,await ExportSnapshot(s));await SaveExportBody("error-503-database",await response.Content.ReadAsStringAsync());
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenActualLaterFetchFailureOrRevocation_WhenStreamingOverKestrel_ThenAbortWithTruncatedJsonAndNoSuccessFooter(bool revoke)
    {
        // Given: a real database cursor, later FETCH boundary, and an actual TCP client.
        var s=await SetupExport(extraRecords:96);var gate=new WireFetchGate(!revoke);var tag="uc54_wire_"+Guid.NewGuid().ToString("N");using var host=ExportWireHost(gate,tag:tag);using var client=host.CreateClient();using var request=ExportRequest(s);
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));Assert.Equal(1,await ExportBackendTransactions(tag));
            await using var stream=await response.Content.ReadAsStreamAsync();var buffer=new byte[4096];var initial=await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));Assert.True(initial>0);
            // When: first prove bytes reached the client, then make the actual next batch fail.
            if(revoke){await using var db=fixture.Context();await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revoked,true));}
            var winner=await ExportSnapshot(s);gate.Release.TrySetResult();var wire=await DrainExportWire(stream,buffer.AsMemory(0,initial));
            // Then: status200 alone is insufficient; transport and a complete valid footer are required.
            Assert.True(wire.Failed);Assert.ThrowsAny<JsonException>(()=>JsonDocument.Parse(wire.Raw));Assert.DoesNotContain("\"complete\":true",wire.Raw);Assert.DoesNotContain("entryCount",wire.Raw);Assert.DoesNotContain("private-export-wire-canary",wire.Raw);Assert.DoesNotContain("persistence_unavailable",wire.Raw);Assert.DoesNotContain("vault_access_denied",wire.Raw);
            await WaitExportBackend(tag,0);Assert.Equal(winner,await ExportSnapshot(s));await SaveExportBody(revoke?"wire-revoked":"wire-failed",wire.Raw);
        }
        finally{gate.Release.TrySetResult();}
    }

    [FunctionalFact]
    public async Task GivenClientDisconnectDuringActualDatabaseWait_WhenExporting_ThenCancelDriverAndReleaseCursorTransaction()
    {
        // Given: later fetch admission waits in a real PostgreSQL pg_sleep on the SAME owning connection.
        var s=await SetupExport(extraRecords:96);var tag="uc54_disconnect_"+Guid.NewGuid().ToString("N");var wait=new WireDatabaseWait();var before=await ExportSnapshot(s);using var host=ExportWireHost(wait,tag:tag);using var client=host.CreateClient();using var request=ExportRequest(s);using var ct=new CancellationTokenSource();
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);Assert.Equal(HttpStatusCode.OK,response.StatusCode);await wait.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));await WaitExportBackend(tag,1,sleeping:true);
        await using var stream=await response.Content.ReadAsStreamAsync();var prefix=new byte[4096];Assert.True(await stream.ReadAsync(prefix,ct.Token)>0);
        // When: cancel the actual response read and close the client connection while PostgreSQL is waiting.
        var pending=ReadExportToEnd(stream,ct.Token).WaitAsync(TimeSpan.FromSeconds(20));ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);response.Dispose();client.Dispose();
        // Then: actual backend transaction/cursor disappear; this cannot be explained by a60second query timeout.
        await WaitExportBackend(tag,0);Assert.Equal(before,await ExportSnapshot(s));
    }

    [FunctionalTheory][InlineData("lease")][InlineData("enumerator")]
    public async Task GivenFailureDisposingAtEof_WhenStreamingOverKestrel_ThenNeverWriteACompleteArchive(string failure)
    {
        // Given: EOF is controlled after a genuine client has received prefix bytes.
        var s=await SetupExport();var lease=new WireFaultLease(s,failure:failure);using var host=ExportWireHost(store:new WireLeaseStore(lease));using var client=host.CreateClient();using var request=ExportRequest(s);
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);Assert.Equal(HttpStatusCode.OK,response.StatusCode);await lease.EndReached.Task.WaitAsync(TimeSpan.FromSeconds(20));await using var stream=await response.Content.ReadAsStreamAsync();var prefix=new byte[4096];var n=await stream.ReadAsync(prefix).AsTask().WaitAsync(TimeSpan.FromSeconds(10));Assert.True(n>0);
            // When/Then: both reader cleanup and owning lease cleanup precede the success footer.
            lease.EndRelease.TrySetResult();var wire=await DrainExportWire(stream,prefix.AsMemory(0,n));Assert.True(wire.Failed);Assert.ThrowsAny<JsonException>(()=>JsonDocument.Parse(wire.Raw));Assert.DoesNotContain("entryCount",wire.Raw);Assert.DoesNotContain("\"complete\":true",wire.Raw);Assert.DoesNotContain("private-export-wire-canary",wire.Raw);await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.True(lease.DisposalAttempted);
        }
        finally{lease.EndRelease.TrySetResult();}
    }

    [FunctionalTheory][InlineData("revision_conflict",409)][InlineData("not_found",404)][InlineData("vault_access_denied",403)]
    [InlineData("persistence_unavailable",503)][InlineData("private-export-wire-canary",503)][InlineData("io",503)]
    public async Task GivenFailureBeforeFirstEntry_WhenExporting_ThenReturnWhitelistedProcessOutputBeforeHeadersAndDispose(string error,int status)
    {
        var s=await SetupExport();var lease=new WireFaultLease(s,initialError:error);using var host=ExportWireHost(store:new WireLeaseStore(lease));using var client=host.CreateClient();using var request=ExportRequest(s);using var response=await client.SendAsync(request);
        await ExportFailure(response,status,error is "private-export-wire-canary" or "io"?"persistence_unavailable":error);Assert.Null(response.Content.Headers.ContentDisposition);Assert.True(lease.DisposalAttempted);Assert.DoesNotContain("private-export-wire-canary",await response.Content.ReadAsStringAsync());
    }

    private WebApplicationFactory<Program> ExportWireHost(DbCommandInterceptor? interceptor=null,string? tag=null,IAccountExportStore? store=null,bool unavailable=false)
    {
        using var db=fixture.Context();var cs=new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()){ApplicationName=tag??"uc54_wire_"+Guid.NewGuid().ToString("N"),Pooling=false,Timeout=unavailable?1:3,CommandTimeout=10};if(unavailable)cs.Port=1;var connection=cs.ConnectionString;
        var host=new WebApplicationFactory<Program>().WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>
        {services.RemoveAll<IAccountExportStore>();services.AddScoped<IAccountExportStore>(_=>store??new AccountExportStore(new ExportWireFactory(connection,interceptor)));}));host.UseKestrel(0);return host;
    }
    private async Task<long> ExportBackendTransactions(string tag,bool sleeping=false)
    {await using var db=fixture.Context();return await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM pg_stat_activity WHERE application_name={tag} AND xact_start IS NOT NULL AND (NOT {sleeping} OR wait_event='PgSleep')").SingleAsync();}
    private async Task WaitExportBackend(string tag,long expected,bool sleeping=false)
    {var watch=Stopwatch.StartNew();long count;do{count=await ExportBackendTransactions(tag,sleeping);if(count==expected)return;await Task.Delay(25);}while(watch.Elapsed<TimeSpan.FromSeconds(10));Assert.Equal(expected,count);}
    private static async Task<string> ReadExportToEnd(Stream stream,CancellationToken ct)
    {using var bytes=new MemoryStream();await stream.CopyToAsync(bytes,ct);return Encoding.UTF8.GetString(bytes.ToArray());}
    private static async Task<(string Raw,bool Failed)> DrainExportWire(Stream stream,ReadOnlyMemory<byte> prefix)
    {
        using var bytes=new MemoryStream();await bytes.WriteAsync(prefix);var failed=false;try{await stream.CopyToAsync(bytes).WaitAsync(TimeSpan.FromSeconds(20));}catch(Exception e)when(e is IOException or HttpRequestException){failed=true;}return(Encoding.UTF8.GetString(bytes.ToArray()),failed);
    }
    private sealed class ExportWireFactory(string connection,DbCommandInterceptor? interceptor):IDbContextFactory<AppDbContext>
    {public AppDbContext CreateDbContext(){var options=new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection);if(interceptor is not null)options.AddInterceptors(interceptor);return new(options.Options);}}
    private sealed class WireFetchGate(bool fail):DbCommandInterceptor
    {
        private int calls;public TaskCompletionSource Reached{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default)
        {if(c.CommandText.TrimStart().StartsWith("FETCH ",StringComparison.OrdinalIgnoreCase)&&++calls==2){Reached.TrySetResult();await Release.Task.WaitAsync(ct);if(fail)throw new IOException("private-export-wire-canary");}return r;}
    }
    private sealed class WireDatabaseWait:DbCommandInterceptor
    {
        private int calls;public TaskCompletionSource Reached{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default)
        {
            if(c.CommandText.TrimStart().StartsWith("FETCH ",StringComparison.OrdinalIgnoreCase)&&++calls==2)
            {await using var sleep=c.Connection!.CreateCommand();sleep.Transaction=c.Transaction;sleep.CommandText="SELECT pg_sleep(60)";sleep.CommandTimeout=60;Reached.TrySetResult();await sleep.ExecuteNonQueryAsync(ct);}return r;
        }
    }
    private sealed class WireLeaseStore(WireFaultLease lease):IAccountExportStore
    {public Task<AccountExportOpenResult> OpenAsync(AccountExportRequest request,CancellationToken ct){ct.ThrowIfCancellationRequested();lease.Arm();return Task.FromResult(new AccountExportOpenResult(Lease:lease));}}
    private sealed class WireFaultLease(ExportState state,string? failure=null,string? initialError=null):IAccountExportLease
    {
        private int attempts;public bool DisposalAttempted=>attempts>0;
        public TaskCompletionSource EndReached{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource EndRelease{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AccountExportHeader Header{get;private set;}=null!;
        private string? InitialError=>initialError;
        private string? Failure=>failure;
        public void Arm()=>Header=new(state.AccountId,state.Actor,1,1,1,true,86400,DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        public IAsyncEnumerable<AccountExportEntry> ReadAsync(CancellationToken ct)=>new FaultReader(this,ct);
        public ValueTask DisposeAsync(){var attempt=++attempts;Disposed.TrySetResult();if(attempt==1&&failure=="lease")throw new IOException("private-export-wire-canary");return ValueTask.CompletedTask;}
        private sealed class FaultReader(WireFaultLease owner,CancellationToken ct):IAsyncEnumerable<AccountExportEntry>,IAsyncEnumerator<AccountExportEntry>
        {
            private int count;
            public AccountExportEntry Current=>new AccountExportAccount([1,2,3]);
            public IAsyncEnumerator<AccountExportEntry> GetAsyncEnumerator(CancellationToken cancellationToken=default)=>this;
            public async ValueTask<bool> MoveNextAsync()
            {ct.ThrowIfCancellationRequested();if(count++==0){if(owner.InitialError is {} error){if(error=="io")throw new IOException("private-export-wire-canary");throw new AccountExportException(error);}return true;}owner.EndReached.TrySetResult();await owner.EndRelease.Task.WaitAsync(ct);return false;}
            public ValueTask DisposeAsync(){if(owner.Failure=="enumerator")throw new IOException("private-export-wire-canary");return ValueTask.CompletedTask;}
        }
    }
}
