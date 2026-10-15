using ArturRios.Cerberus.TestSupport;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

public partial class RetentionMaintenanceTests
{
    [FunctionalTheory][InlineData("streamDelete")][InlineData("profileUpdate")][InlineData("sequenceUsage")]
    public async Task GivenWorkerRoleLacksOneRequiredRight_WhenPreparingPass_ThenRefuseBeforeAnyQueueOrNativeChange(string missing)
    {
        using var keys=new ProtectionFixture();var s=await Owner(keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));var before=await TrashRestoreSetup.Snapshot(fixture,s);await using var db=fixture.CreateContext();
        var target=await RestrictedRole(missing);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>Run(target));Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="record"&&x.ResourceId==root.ResourceId));
    }
    [FunctionalFact]
    public async Task GivenNoExpiredRows_WhenMaintaining_ThenReturnZeroCountsAndNoDatabaseChanges()
    {
        using var keys=new ProtectionFixture();var s=await Owner(keys);var before=await TrashRestoreSetup.Snapshot(fixture,s);var result=await Run();Assert.Equal(0,result.Completed);Assert.Equal(0,result.Failed);Assert.Equal(0,result.ScheduledJobs);Assert.Equal(0,result.ExpiredStreams);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));
    }
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenRestrictedRoleWithoutNativeInsert_WhenRepairingAndPurging_ThenCompleteNativeExpiry(string kind)
    {
        if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException();
        using var keys=new ProtectionFixture();var s=await Owner(keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));await using var db=fixture.CreateContext();await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+root.OperationId).ExecuteDeleteAsync();var before=await TrashRestoreSetup.Protected(fixture,s);
        var target=await RestrictedRole(null);var directory=Path.Combine(Path.GetTempPath(),"cerberus-restricted-retention-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try
        {
            var ledger=new FileErasureLedger(directory);var runner=new RetentionRunner(new RetentionWorkStore(target),new RetentionExecutor([new TrashExpiryHandler(target,ledger)]),TimeProvider.System,new RetentionMaintenance(target));
            var result=await runner.RunOnceAsync(TimeSpan.FromMinutes(5),100,default);Assert.Equal(1,result.ScheduledJobs);Assert.Equal(1,result.Completed);Assert.Equal(0,result.Failed);
            Assert.False(await db.TrashOperations.AnyAsync(x=>x.PublicId==root.OperationId));Assert.True(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="trash/"+root.OperationId&&x.CompletedAt!=null));Assert.Equal(before,await TrashRestoreSetup.Protected(fixture,s));Assert.Single(Directory.GetFiles(directory,"*.json"));
        }
        finally{Directory.Delete(directory,true);}
    }
    [FunctionalFact]
    public async Task GivenRestrictedRoleAndForeignSharedData_WhenRepairingClosureExpiry_ThenPurgeOwnedAccountAndPreserveForeignNativeBytes()
    {
        if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException();
        using var keys=new ProtectionFixture();using var foreignKeys=new ProtectionFixture();var s=await Owner(keys);var foreign=await Owner(foreignKeys);await AssociationSetup.Items(fixture,s);await AssociationSetup.Profile(fixture,s,keys);var items=await AssociationSetup.Items(fixture,foreign);var grant=await AssociationSetup.Grant(fixture,foreign,foreignKeys,s,keys,items.Collection);
        Assert.Null((await new AccountClosureStore(fixture).RequestAsync(new(s.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),1),default)).Error);await using var db=fixture.CreateContext();var due=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp()-interval '1 second' AS \"Value\"").SingleAsync();await db.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ClosureRequestedAt,due.AddDays(-30)).SetProperty(a=>a.ClosurePurgeAt,due));await db.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+s.AccountId).ExecuteDeleteAsync();var before=await TrashRestoreSetup.Snapshot(fixture,foreign);
        var target=await RestrictedRole(null);var directory=Path.Combine(Path.GetTempPath(),"cerberus-restricted-closure-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try
        {
            var ledger=new FileErasureLedger(directory);var runner=new RetentionRunner(new RetentionWorkStore(target),new RetentionExecutor([new AccountExpiryHandler(target,ledger)]),TimeProvider.System,new RetentionMaintenance(target));var result=await runner.RunOnceAsync(TimeSpan.FromMinutes(5),100,default);Assert.Equal(1,result.ScheduledJobs);Assert.Equal(1,result.Completed);Assert.Equal(0,result.Failed);
            Assert.False(await db.Accounts.AnyAsync(x=>x.Id==s.InternalId));Assert.False(await db.CollectionGrants.AnyAsync(x=>x.Id==grant.Id));Assert.True(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="account-purge/"+s.AccountId&&x.CompletedAt!=null));Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,foreign));Assert.True(Directory.GetFiles(directory,"*.json").Length>=6);
        }
        finally{Directory.Delete(directory,true);}
    }
    private async Task<RestrictedRetentionFactory> RestrictedRole(string? missing)
    {
        await using var db=fixture.CreateContext();var role="retention_fixture_"+Guid.NewGuid().ToString("N");using var identifiers=new NpgsqlCommandBuilder();var quoted=identifiers.QuoteIdentifier(role);
        var create="CREATE ROLE "+quoted+" LOGIN PASSWORD 'fixture-retention-role-password'";await db.Database.ExecuteSqlRawAsync(create);
        var schema="GRANT USAGE ON SCHEMA cerberus TO "+quoted;await db.Database.ExecuteSqlRawAsync(schema);
        var tables="GRANT SELECT,UPDATE,DELETE ON ALL TABLES IN SCHEMA cerberus TO "+quoted;await db.Database.ExecuteSqlRawAsync(tables);
        var insert="GRANT INSERT ON cerberus.retention_work_item,cerberus.terminal_erasure TO "+quoted;await db.Database.ExecuteSqlRawAsync(insert);
        var sequence="GRANT USAGE ON SEQUENCE cerberus.server_sequence TO "+quoted;await db.Database.ExecuteSqlRawAsync(sequence);
        if(missing is not null){var revoke=(missing switch{"streamDelete"=>"REVOKE DELETE ON cerberus.sync_stream FROM ","profileUpdate"=>"REVOKE UPDATE ON cerberus.profile FROM ",_=>"REVOKE USAGE ON SEQUENCE cerberus.server_sequence FROM "})+quoted;await db.Database.ExecuteSqlRawAsync(revoke);}
        var connection=new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()){Username=role,Password="fixture-retention-role-password",Pooling=false};return new(connection.ConnectionString);
    }
    private sealed class RestrictedRetentionFactory(string connection):IDbContextFactory<AppDbContext>
    {public AppDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);}
}
