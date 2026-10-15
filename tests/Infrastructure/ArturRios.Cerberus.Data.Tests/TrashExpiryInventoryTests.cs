using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

public partial class TrashExpiryHandlerTests
{
    [FunctionalFact]
    public async Task GivenIndependentRetainedOperations_WhenExpiringOne_ThenMaterializeOnlyItsNativeMetadata()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));
        var plan=new ExpiryInventoryPlan(fixture);await Executor(factory:new ProfileSetup.Factory(fixture,plan)).ExecuteAsync(await Claim(root),default);
        Assert.NotNull(plan.Plan);using var json=JsonDocument.Parse(plan.Plan);Assert.Equal(1m,json.RootElement[0].GetProperty("Plan").GetProperty("Actual Rows").GetDecimal());
    }
    [UnitTheory][InlineData("trash")][InlineData("trash/")][InlineData("trash/00000000-0000-0000-0000-000000000000")]
    [InlineData("trash/123456781234123412341234567890ab")][InlineData("trash/12345678-1234-1234-1234-1234567890AB")]
    [InlineData("trash/12345678-1234-1234-1234-1234567890ab/extra")]
    public async Task GivenMalformedExpiryKey_WhenExecuting_ThenRejectBeforeDatabaseOrLedger(string key)
    {
        var claim=new RetentionClaim(Guid.NewGuid(),Guid.NewGuid(),key,DateTimeOffset.UtcNow.AddMinutes(5));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new TrashExpiryHandler(new NoExpiryDatabase(),Ledger).ExecuteAsync(claim,default));Assert.Empty(Directory.GetFiles(directory,"*.json"));
    }
    private sealed class NoExpiryDatabase:IDbContextFactory<AppDbContext>
    {public AppDbContext CreateDbContext()=>throw new IOException("Unexpected database access");}
    private sealed class ExpiryInventoryPlan(PostgresFixture postgres):DbCommandInterceptor
    {
        internal string? Plan;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            if(Plan is null&&command.CommandText.Contains("r.public_id resource_id")&&command.CommandText.Contains("LEFT JOIN cerberus.terminal_erasure"))
            {
                await using var db=postgres.CreateContext();await db.Database.OpenConnectionAsync(ct);await using var explain=db.Database.GetDbConnection().CreateCommand();explain.CommandText="EXPLAIN (ANALYZE, FORMAT JSON) "+command.CommandText;
                foreach(var parameter in command.Parameters.Cast<NpgsqlParameter>())explain.Parameters.Add(parameter.Clone());Plan=(string)(await explain.ExecuteScalarAsync(ct))!;
            }
            return result;
        }
    }
}
