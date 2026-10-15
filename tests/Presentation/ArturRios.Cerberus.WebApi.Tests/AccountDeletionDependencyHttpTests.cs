using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Data;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ArturRios.Cerberus.WebApi.Tests;

public partial class AccountDeletionHttpTests
{
    [FunctionalFact]
    public async Task GivenLedgerOutageAfterIntent_WhenDeletingThroughHost_Then503KeepsIrreversibleIntentAndDeniesCancellation()
    {
        var s=await Setup();using var host=new WebApplicationFactory<Program>().WithWebHostBuilder(b=>b.ConfigureTestServices(services=>
        {services.RemoveAll<IErasureLedger>();services.AddSingleton<IErasureLedger>(new UnavailableLedger());}));using var client=host.CreateClient();
        using var request=Request(s);using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);
        var raw=await response.Content.ReadAsStringAsync();Assert.Contains("persistence_unavailable",raw);Assert.DoesNotContain("fixture",raw);Assert.DoesNotContain(s.Handle,raw);
        await using var db=fixture.Context();Assert.True(await db.Accounts.AnyAsync(x=>x.PublicId==s.AccountId));Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==s.AccountId));
        Assert.True(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="account-erase/"+s.AccountId&&x.CompletedAt==null));
        var before=await Snapshot(s);using var cancel=new HttpRequestMessage(HttpMethod.Post,"/api/accounts/me/closure/cancel"){Content=new StringContent("{\"expectedRevision\":2}",Encoding.UTF8,"application/json")};cancel.Headers.Authorization=request.Headers.Authorization;
        using var denied=await client.SendAsync(cancel);Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenUnavailableDatabase_WhenDeleting_Then503AndNoMutationAfterRecovery()
    {
        var s=await Setup();var before=await Snapshot(s);
        await using var db=fixture.Context();
        var unreachable=new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()){Port=1,Timeout=1,Pooling=false};
        using var host=new WebApplicationFactory<Program>().WithWebHostBuilder(b=>b.ConfigureTestServices(services=>
        {
            services.RemoveAll<IAccountDeletionStore>();services.AddScoped<IAccountDeletionStore>(p=>new AccountDeletionStore(new TestFactory(unreachable.ConnectionString),p.GetRequiredService<IErasureLedger>(),p.GetRequiredService<CerberusOptions>(),p.GetRequiredService<TimeProvider>()));
        }));using var client=host.CreateClient();using var request=Request(s);using var response=await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.DoesNotContain("connection",await response.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenWorkerWinsClaimImmediatelyAfterIntent_WhenDeletingThroughHost_Then202TracksPendingErasureUntilWorkerCompletes()
    {
        var s=await Setup();var interceptor=new ClaimCommittedIntent(fixture,s.AccountId);string connection;await using(var db=fixture.Context())connection=db.Database.GetConnectionString()!;
        var factory=new TestFactory(connection,interceptor);
        using var host=new WebApplicationFactory<Program>().WithWebHostBuilder(b=>b.ConfigureTestServices(services=>
        {
            services.RemoveAll<IAccountDeletionStore>();services.AddScoped<IAccountDeletionStore>(p=>new AccountDeletionStore(factory,p.GetRequiredService<IErasureLedger>(),p.GetRequiredService<CerberusOptions>(),p.GetRequiredService<TimeProvider>()));
        }));using var client=host.CreateClient();using var request=Request(s);using var response=await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);var raw=await response.Content.ReadAsStringAsync();
        using var doc=JsonDocument.Parse(raw);Assert.Equal("pending",doc.RootElement.GetProperty("data").GetProperty("state").GetString());Assert.NotNull(interceptor.Claim);
        var folder=Path.Combine(AppContext.BaseDirectory,"account-deletion-contract-fixtures");Directory.CreateDirectory(folder);await File.WriteAllTextAsync(Path.Combine(folder,"pending.json"),raw);
        await using var check=fixture.Context();Assert.True(await check.Accounts.AnyAsync(x=>x.PublicId==s.AccountId));Assert.True(await check.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==s.AccountId));
        var plain=new TestFactory(connection);await new AccountPurgeHandler(plain,host.Services.GetRequiredService<IErasureLedger>()).ExecuteAsync(interceptor.Claim!,default);
        Assert.True(await new RetentionWorkStore(plain).TryCompleteAsync(interceptor.Claim!,DateTimeOffset.UtcNow,default));Assert.False(await check.Accounts.AnyAsync(x=>x.PublicId==s.AccountId));
    }
    private sealed class UnavailableLedger:IErasureLedger
    {
        public Task RecordAsync(ErasureEntry entry,CancellationToken ct)=>throw new IOException("fixture ledger unavailable");
        public IAsyncEnumerable<ErasureEntry> ReadAsync(CancellationToken ct)=>throw new IOException("fixture ledger unavailable");
    }
    private sealed class TestFactory(string connection,IInterceptor? interceptor=null):IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {var b=new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection);if(interceptor is not null)b.AddInterceptors(interceptor);return new(b.Options);}
    }
    private sealed class ClaimCommittedIntent(RegistrationApiFixture fixture,Guid account):DbTransactionInterceptor
    {
        public RetentionClaim? Claim{get;private set;}private bool attempted;
        public override async Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData eventData,CancellationToken cancellationToken=default)
        {
            if(attempted)return;attempted=true;await using var db=fixture.Context();var job=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="account-erase/"+account,cancellationToken);
            Claim=await new RetentionWorkStore(new TestFactory(db.Database.GetConnectionString()!)).TryClaimAsync(job.PublicId,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(1),cancellationToken);Assert.NotNull(Claim);
        }
    }
}
