using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
namespace ArturRios.Cerberus.Data.Tests;

public partial class TrashEmptyStoreTests
{
    [FunctionalFact]
    public async Task GivenManyUnrelatedActiveRecordsAndOneTrash_WhenEmptying_ThenInventoryMaterializationIsBoundedToTrash()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");var env=JsonSerializer.SerializeToUtf8Bytes(ProfileSetup.Envelope(),ProtectionFixture.Json);
        await using(var db=fixture.CreateContext()){db.Records.AddRange(Enumerable.Range(0,500).Select(_=>new VaultRecord{AccountId=s.InternalId,PublicId=Guid.NewGuid(),Envelope=env,EditedAt=DateTimeOffset.UnixEpoch}));await db.SaveChangesAsync();}
        var capture=new InventoryPlan(fixture);var r=await Store(factory:new ProfileSetup.Factory(fixture,capture)).EmptyAsync(Request(s),default);Assert.Null(r.Error);Assert.Equal(1,r.Data!.TotalPurged);Assert.NotNull(capture.Plan);
        using var plan=JsonDocument.Parse(capture.Plan);Assert.Equal(1,plan.RootElement[0].GetProperty("Plan").GetProperty("Actual Rows").GetDecimal());
        await using var check=fixture.CreateContext();Assert.Equal(500,await check.Records.CountAsync(x=>x.AccountId==s.InternalId&&x.DeletedAt==null));Assert.False(await Exists(root));
    }
    private sealed class InventoryPlan(PostgresFixture postgres):DbCommandInterceptor
    {
        internal string? Plan{get;private set;}
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default)
        {
            if(Plan is null&&c.CommandText.Contains("r.public_id resource_id")&&c.CommandText.Contains("LEFT JOIN cerberus.terminal_erasure")){
                await using var db=postgres.CreateContext();await db.Database.OpenConnectionAsync(ct);await using var explain=db.Database.GetDbConnection().CreateCommand();explain.CommandText="EXPLAIN (ANALYZE, FORMAT JSON) "+c.CommandText;foreach(var p in c.Parameters.Cast<NpgsqlParameter>())explain.Parameters.Add(p.Clone());Plan=(string)(await explain.ExecuteScalarAsync(ct))!;}
            return r;
        }
    }
}
