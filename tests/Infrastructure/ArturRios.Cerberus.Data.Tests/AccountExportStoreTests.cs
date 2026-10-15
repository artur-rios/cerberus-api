using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Accounts.Export;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("Account export PostgreSQL")]
public partial class AccountExportStoreTests(PostgresFixture fixture):IAsyncLifetime
{
    private readonly List<long> owners=[];

    [FunctionalFact]
    public async Task GivenCurrentWideOwner_WhenExporting_ThenPreserveOwnedCiphertextKeysAndMetadataWithoutMutations()
    {
        // Given: complete own and unrelated native graphs with distinct ciphertext.
        var w=await World();var other=await World();var before=await Snapshot(w);var foreignBefore=await Snapshot(other);
        // When: consume the actual PostgreSQL export lease.
        var result=await Capture(w);
        // Then: immutable context and original bytes survive; no foreign vault is copied.
        Assert.Equal(w.Owner.AccountId,result.Header.AccountId);Assert.Equal(w.Owner.Actor,result.Header.IdentityId);
        Assert.Equal(1,result.Header.Revision);Assert.Equal(1,result.Header.PolicyRevision);Assert.Equal(1,result.Header.RevocationGeneration);
        Assert.True(result.Header.RenewalEnabled);Assert.Equal(86400,result.Header.RenewalIntervalSeconds);Assert.Equal(TimeSpan.Zero,result.Header.ServerTime.Offset);Assert.Equal(0,result.Header.ServerTime.Ticks%10);
        Assert.Equal(w.Owner.AccountEnvelope,result.Entries.OfType<AccountExportAccount>().Single().Details);
        await using var db=fixture.CreateContext();var protection=await db.VaultProtections.AsNoTracking().SingleAsync(x=>x.AccountId==w.Owner.InternalId);
        Assert.Equal(protection.Material,result.Entries.OfType<AccountExportProtection>().Single().Material);
        var p=result.Entries.OfType<AccountExportProfile>().Single();Assert.Equal(w.Profile.PublicId,p.ResourceId);Assert.Equal(w.Profile.Envelope,p.Envelope);Assert.Equal(w.Profile.KeyWrappers,p.KeyWrappers);
        var r=result.Entries.OfType<AccountExportRecord>().Single();Assert.Equal(w.Record.PublicId,r.ResourceId);Assert.Equal(w.Record.Envelope,r.Envelope);Assert.Equal(w.Folder.PublicId,r.FolderId);Assert.Equal(w.Record.ServerSequence,r.ServerSequence);
        Assert.Equal(w.Folder.Envelope,result.Entries.OfType<AccountExportFolder>().Single().Envelope);Assert.Equal(w.Collection.Envelope,result.Entries.OfType<AccountExportCollection>().Single().Envelope);
        var json=JsonSerializer.Serialize(result.Entries,ProtectionFixture.Json);Assert.DoesNotContain(other.Owner.AccountId.ToString(),json);Assert.DoesNotContain(other.Profile.PublicId.ToString(),json);Assert.DoesNotContain(other.Record.PublicId.ToString(),json);Assert.DoesNotContain(w.Owner.Verifier,json);
        Assert.Equal(before,await Snapshot(w));Assert.Equal(foreignBefore,await Snapshot(other));
    }

    [FunctionalTheory]
    [InlineData("missingOwner","not_found")][InlineData("closing","not_found")][InlineData("terminalOwner","not_found")]
    [InlineData("foreignVerifier","vault_access_denied")][InlineData("revoked","vault_access_denied")]
    [InlineData("expired","vault_access_denied")][InlineData("profile","vault_access_denied")]
    [InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")]
    [InlineData("futureIssue","vault_access_denied")][InlineData("missingExpiry","vault_access_denied")]
    [InlineData("disabledWithExpiry","vault_access_denied")]
    public async Task GivenUnavailableCurrentAuthority_WhenExporting_ThenRefuseWithoutAnyNativeChanges(string invalid,string error)
    {
        // Given: each denial is a real persisted state, not an accepting mock.
        var w=await World();var request=Request(w);await using(var db=fixture.CreateContext())
        {
            switch(invalid)
            {
                case "missingOwner":request=request with{Actor=Guid.NewGuid()};break;
                case "closing":await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));break;
                case "terminalOwner":db.TerminalErasures.Add(new(){ResourceKind="account",ResourceId=w.Owner.AccountId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();break;
                case "foreignVerifier":var other=await World();request=request with{AccessVerifier=other.Owner.Verifier};break;
                case "revoked":await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.Revoked,true));break;
                case "expired":await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.ExpiresAt,DateTimeOffset.UtcNow.AddHours(-1)));break;
                case "profile":await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.ProfileId,w.Profile.Id));break;
                case "policy":await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.PolicyRevision,2));break;
                case "generation":await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.RevocationGeneration,2));break;
                case "futureIssue":await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.IssuedAt,DateTimeOffset.UtcNow.AddHours(1)));break;
                case "missingExpiry":await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.ExpiresAt,(DateTimeOffset?)null));break;
                case "disabledWithExpiry":await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.RenewalEnabled,false).SetProperty(a=>a.RenewalInterval,(TimeSpan?)null));break;
            }
        }
        var before=await Snapshot(w);
        // When/Then: no lease or mutation accompanies an expected refusal.
        var result=await new AccountExportStore(fixture).OpenAsync(request,default);
        Assert.Equal(error,result.Error);Assert.Null(result.Lease);Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalTheory][InlineData("revision","revision_conflict")][InlineData("stale","fresh_authentication_required")][InlineData("future","fresh_authentication_required")]
    public async Task GivenStaleRevisionOrAuthentication_WhenExporting_ThenKeepTheWinningGraphUnchanged(string invalid,string error)
    {
        // Given/When: compare client state to real database admission time/state.
        var w=await World();var before=await Snapshot(w);var request=Request(w);
        request=invalid switch{"revision"=>request with{ExpectedRevision=2},"stale"=>request with{AuthenticationIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-60},_=>request with{AuthenticationIssuedAt=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}};
        var result=await new AccountExportStore(fixture).OpenAsync(request,default);
        // Then: failure is stable and read-only.
        Assert.Equal(error,result.Error);Assert.Null(result.Lease);Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalFact]
    public async Task GivenDisabledRenewalWithNoExpiry_WhenExporting_ThenRetainTheExplicitNullPolicy()
    {
        // Given: disabled renewal has no invented interval/session expiry.
        var w=await World();await using(var db=fixture.CreateContext())
        {await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.RenewalEnabled,false).SetProperty(a=>a.RenewalInterval,(TimeSpan?)null));await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.ExpiresAt,(DateTimeOffset?)null));}
        var before=await Snapshot(w);
        // When/Then: the full native package remains available and unchanged.
        var result=await Capture(w);Assert.False(result.Header.RenewalEnabled);Assert.Null(result.Header.RenewalIntervalSeconds);Assert.Single(result.Entries.OfType<AccountExportRecord>());Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalTheory][InlineData("fraction")][InlineData("zero")][InlineData("negative")][InlineData("missing")][InlineData("disabledInterval")]
    public async Task GivenCorruptStoredPolicy_WhenExporting_ThenRefuseRatherThanRoundOrInventPortableMetadata(string invalid)
    {
        // Given: a persisted policy outside the approved whole-second/null contract.
        var w=await World();await using(var db=fixture.CreateContext())
        {
            TimeSpan? interval=invalid switch{"fraction"=>TimeSpan.FromSeconds(1.5),"zero"=>TimeSpan.Zero,"negative"=>TimeSpan.FromSeconds(-1),"missing"=>null,_=>TimeSpan.FromHours(24)};
            await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.RenewalInterval,interval).SetProperty(a=>a.RenewalEnabled,invalid!="disabledInterval"));
            if(invalid=="disabledInterval")await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.ExpiresAt,(DateTimeOffset?)null));
        }
        var before=await Snapshot(w);
        // When/Then: no rounded/incoherent policy is admitted to a successful archive.
        var result=await new AccountExportStore(fixture).OpenAsync(Request(w),default);Assert.Equal("persistence_unavailable",result.Error);Assert.Null(result.Lease);Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalTheory][InlineData("account")][InlineData("protection")][InlineData("profile")][InlineData("profileKeys")][InlineData("record")][InlineData("folder")][InlineData("collection")]
    public async Task GivenOpaqueLegacyStoredBytes_WhenExporting_ThenTransportThemExactlyWithoutReinterpretingCrypto(string kind)
    {
        // Given: unsupported/non-UTF8 opaque stored bytes belong to the current owner.
        var w=await World();byte[] bytes=[255,0,17,128,45];await using(var db=fixture.CreateContext())
        {
            switch(kind)
            {
                case "account":await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.DetailsEnvelope,bytes));break;
                case "protection":await db.VaultProtections.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Material,bytes));break;
                case "profile":await db.Profiles.Where(x=>x.Id==w.Profile.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Envelope,bytes));break;
                case "profileKeys":await db.Profiles.Where(x=>x.Id==w.Profile.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.KeyWrappers,bytes));break;
                case "record":await db.Records.Where(x=>x.Id==w.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,bytes));break;
                case "folder":await db.Folders.Where(x=>x.Id==w.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.Envelope,bytes));break;
                case "collection":await db.Collections.Where(x=>x.Id==w.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,bytes));break;
            }
        }
        var before=await Snapshot(w);
        // When/Then: this is byte transport, never a decryptability assertion.
        var result=await Capture(w);var observed=kind switch
        {"account"=>result.Entries.OfType<AccountExportAccount>().Single().Details,"protection"=>result.Entries.OfType<AccountExportProtection>().Single().Material,
            "profile"=>result.Entries.OfType<AccountExportProfile>().Single().Envelope,"profileKeys"=>result.Entries.OfType<AccountExportProfile>().Single().KeyWrappers,
            "record"=>result.Entries.OfType<AccountExportRecord>().Single().Envelope,"folder"=>result.Entries.OfType<AccountExportFolder>().Single().Envelope,_=>result.Entries.OfType<AccountExportCollection>().Single().Envelope};
        Assert.Equal(bytes,observed);Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalFact]
    public async Task GivenHistoricalWrapperRevision_WhenExporting_ThenKeepItsOriginalBytesBesideCurrentResourceRevision()
    {
        // Given: profile edits may advance row revision without rewriting its wrapper context.
        var w=await World();await using(var db=fixture.CreateContext())await db.Profiles.Where(x=>x.Id==w.Profile.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Revision,9));var before=await Snapshot(w);
        // When/Then: preserve the signed original grant revision rather than substituting9.
        var result=await Capture(w);var p=result.Entries.OfType<AccountExportProfile>().Single();Assert.Equal(9,p.Revision);Assert.Equal(w.Profile.KeyWrappers,p.KeyWrappers);
        using var wrappers=JsonDocument.Parse(p.KeyWrappers);Assert.Equal(1,wrappers.RootElement.GetProperty("masterKeyWrapper").GetProperty("grantRevision").GetInt64());Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalFact]
    public async Task GivenMoreThanTwoFetchBatches_WhenExporting_ThenReturnAllOwnedRowsWithoutPageSizeTruncation()
    {
        // Given/When: total export size exceeds the ordinary100-item API page and3batches.
        var w=await World(extraRecords:120);var before=await Snapshot(w);var result=await Capture(w);
        // Then: every distinct record is included; pagination is internal only.
        Assert.Equal(121,result.Entries.OfType<AccountExportRecord>().Count());Assert.Equal(121,result.Entries.OfType<AccountExportRecord>().Select(x=>x.ResourceId).Distinct().Count());Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalFact]
    // Identifiers cannot be SQL parameters: the role is a fixed prefix plus Guid N,
    // and table identifiers below are fixed test literals, never external input.
#pragma warning disable EF1003
    public async Task GivenSelectOnlyNonOwnerRole_WhenExporting_ThenUseAReadOnlyTransactionWithoutWriteOrSecretTableCapabilities()
    {
        // Given: a real LOGIN has only the tables needed for authority/portable projections.
        var w=await World();var before=await Snapshot(w);var role="uc54_read_"+Guid.NewGuid().ToString("N");var probe=new ReadMode();
        await using var root=fixture.CreateContext();await root.Database.ExecuteSqlRawAsync("CREATE ROLE "+role+" LOGIN PASSWORD 'fixture-only-export-password'");
        try
        {
            await root.Database.ExecuteSqlRawAsync("GRANT USAGE ON SCHEMA cerberus TO "+role);
            var tables=new[]{"account","vault_access_session","vault_protection","terminal_erasure","profile","record","folder","collection","profile_record","profile_folder","profile_collection","collection_record","collection_folder","collection_grant","software_grant","software_grant_record","software_grant_collection","trash_operation","trash_entry"};
            await root.Database.ExecuteSqlRawAsync("GRANT SELECT ON "+string.Join(',',tables.Select(x=>"cerberus."+x))+" TO "+role);
            var target=Factory(probe,change:b=>{b.Username=role;b.Password="fixture-only-export-password";});
            // When/Then: successful native export proves no UPDATE locks, DML or sequence usage.
            var result=await Capture(w,target);Assert.Single(result.Entries.OfType<AccountExportRecord>());Assert.Equal("on",probe.Mode);Assert.Equal(before,await Snapshot(w));
        }
        finally{await root.Database.ExecuteSqlRawAsync("DROP OWNED BY "+role);await root.Database.ExecuteSqlRawAsync("DROP ROLE "+role);}
    }
#pragma warning restore EF1003

    [FunctionalFact]
    public async Task GivenUnavailableDatabase_WhenOpeningExport_ThenReturnOnlyUnavailableAndLeaveHealthyDataUnchanged()
    {
        // Given/When: an actual missing database fails PostgreSQL connection admission.
        var w=await World();var before=await Snapshot(w);var result=await new AccountExportStore(Factory(change:b=>b.Database="missing_uc54_"+Guid.NewGuid().ToString("N"))).OpenAsync(Request(w),default);
        // Then: no connection details or lease escape.
        Assert.Equal("persistence_unavailable",result.Error);Assert.Null(result.Lease);Assert.Equal(before,await Snapshot(w));
    }

    [FunctionalFact]
    public async Task GivenCallerCancellation_WhenOpeningExport_ThenPropagateAndLeaveNativeStateUnchanged()
    {
        // Given/When/Then: canceled callers cannot acquire a successful empty archive.
        var w=await World();var before=await Snapshot(w);using var ct=new CancellationTokenSource();ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new AccountExportStore(fixture).OpenAsync(Request(w),ct.Token));Assert.Equal(before,await Snapshot(w));
    }

    private sealed record Graph(ProfileSetup.State Owner,Profile Profile,VaultRecord Record,VaultFolder Folder,VaultCollection Collection);
    private sealed record Captured(AccountExportHeader Header,List<AccountExportEntry> Entries);
    private async Task<Graph> World(int extraRecords=0)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);owners.Add(s.InternalId);var pid=await AssociationSetup.Profile(fixture,s,keys);var items=await AssociationSetup.Items(fixture,s);
        await using var db=fixture.CreateContext();await db.Records.Where(x=>x.Id==items.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,items.Folder.Id));items.Record.FolderId=items.Folder.Id;
        for(var i=0;i<extraRecords;i++)db.Records.Add(new(){AccountId=s.InternalId,PublicId=Guid.NewGuid(),Envelope=JsonSerializer.SerializeToUtf8Bytes(ProfileSetup.Envelope(),ProtectionFixture.Json),EditedAt=items.Record.EditedAt});
        await db.SaveChangesAsync();return new(s,await db.Profiles.AsNoTracking().SingleAsync(x=>x.Id==pid),items.Record,items.Folder,items.Collection);
    }
    private static AccountExportRequest Request(Graph w)=>new(w.Owner.Actor,w.Owner.Verifier,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),1);
    private async Task<IAccountExportLease> Open(Graph w,IDbContextFactory<AppDbContext>? target=null)
    {var result=await new AccountExportStore(target??fixture).OpenAsync(Request(w),default);Assert.Null(result.Error);return Assert.IsAssignableFrom<IAccountExportLease>(result.Lease);}
    private async Task<Captured> Capture(Graph w,IDbContextFactory<AppDbContext>? target=null)
    {await using var lease=await Open(w,target);var entries=new List<AccountExportEntry>();await foreach(var e in lease.ReadAsync(default))entries.Add(e);return new(lease.Header,entries);}
    private IDbContextFactory<AppDbContext> Factory(DbCommandInterceptor? interceptor=null,string? tag=null,Action<NpgsqlConnectionStringBuilder>? change=null)
    {
        using var db=fixture.CreateContext();var cs=new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()){ApplicationName=tag??"uc54_export_"+Guid.NewGuid().ToString("N"),Pooling=false,Timeout=3,CommandTimeout=10};change?.Invoke(cs);
        var options=new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs.ConnectionString);if(interceptor is not null)options.AddInterceptors(interceptor);return new ProfileSetup.Factory(options.Options);
    }
    private async Task<string> Snapshot(Graph w)
    {
        await using var db=fixture.CreateContext();var id=w.Owner.InternalId;
        return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id),Protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==id),
            Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==id).OrderBy(x=>x.Id).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==id).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==id).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==id).OrderBy(x=>x.Id).ToArrayAsync(),
            Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==id).OrderBy(x=>x.Id).ToArrayAsync(),Markers=await db.TerminalErasures.AsNoTracking().OrderBy(x=>x.ResourceKind).ThenBy(x=>x.ResourceId).ToArrayAsync()},ProtectionFixture.Json);
    }
    private async Task<long> ActiveTransactions(string tag)
    {await using var db=fixture.CreateContext();return await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM pg_stat_activity WHERE application_name={tag} AND xact_start IS NOT NULL").SingleAsync();}
    private sealed class ReadMode:DbCommandInterceptor
    {
        public string? Mode{get;private set;}
        private async Task Check(DbCommand command,CancellationToken ct)
        {if(Mode is null&&command.CommandText.TrimStart().StartsWith("DECLARE ",StringComparison.OrdinalIgnoreCase)){await using var probe=command.Connection!.CreateCommand();probe.Transaction=command.Transaction;probe.CommandText="SHOW transaction_read_only";Mode=Convert.ToString(await probe.ExecuteScalarAsync(ct));}}
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){await Check(c,ct);return r;}
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){await Check(c,ct);return r;}
    }
    public Task InitializeAsync()=>Task.CompletedTask;
    public async Task DisposeAsync(){if(owners.Count==0)return;await using var db=fixture.CreateContext();await db.Accounts.Where(x=>owners.Contains(x.Id)).ExecuteDeleteAsync();}
}

[CollectionDefinition("Account export PostgreSQL",DisableParallelization=true)]
public sealed class AccountExportPostgresCollection:ICollectionFixture<PostgresFixture>;
