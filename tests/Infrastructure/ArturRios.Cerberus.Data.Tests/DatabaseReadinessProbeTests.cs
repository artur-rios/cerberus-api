using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public sealed class DatabaseReadinessProbeTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenConnectableRoleWithOrWithoutRequiredSelect_WhenProbing_ThenCheckCapabilityAndPreserveCiphertext(bool granted)
    {
        var id=Guid.NewGuid();await using var setup=fixture.CreateContext();setup.Accounts.Add(new Account{PublicId=id,HeimdallPublicId=Guid.NewGuid(),DetailsEnvelope=[7,8,9],Revision=4});await setup.SaveChangesAsync();
        var name="health_"+Guid.NewGuid().ToString("N");var cs=new NpgsqlConnectionStringBuilder(setup.Database.GetConnectionString()){Username=name,Password="fixture-health-only",Pooling=false,ApplicationName=name};
        await Sql(setup,$"CREATE ROLE {name} LOGIN PASSWORD 'fixture-health-only'; GRANT USAGE ON SCHEMA cerberus TO {name};"+(granted?$" GRANT SELECT(public_id) ON cerberus.account TO {name};":""));
        try
        {
            await using(var connection=new NpgsqlConnection(cs.ConnectionString))await connection.OpenAsync();
            Assert.Equal(granted,await new DatabaseReadinessProbe(new Factory(cs.ConnectionString)).CheckAsync(default));
            await setup.Entry(await setup.Accounts.SingleAsync(x=>x.PublicId==id)).ReloadAsync();var after=await setup.Accounts.SingleAsync(x=>x.PublicId==id);Assert.Equal(4,after.Revision);Assert.Equal(new byte[]{7,8,9},after.DetailsEnvelope);
            Assert.Equal(0,await Active(name));
        }
        finally{await Sql(setup,$"DROP OWNED BY {name}; DROP ROLE {name};");}
    }
    [FunctionalFact]
    public async Task GivenConnectionButMissingRequiredSchema_WhenProbing_ThenUnhealthyWithoutThrowing()
    {
        await using var setup=fixture.CreateContext();await setup.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.account RENAME TO fixture_health_account");
        try{Assert.False(await new DatabaseReadinessProbe(fixture).CheckAsync(default));}
        finally{await setup.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_health_account RENAME TO account");}
    }
    [FunctionalFact]
    public async Task GivenUnreachableDatabase_WhenProbing_ThenUnhealthy()
    {Assert.False(await new DatabaseReadinessProbe(new Factory("Host=127.0.0.1;Port=1;Database=fixture;Username=fixture;Password=fixture;Timeout=1;Pooling=false")).CheckAsync(default));}
    [FunctionalFact]
    public async Task GivenCanceledCaller_WhenProbing_ThenPropagateWithoutOpeningBackend()
    {using var canceled=new CancellationTokenSource();canceled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new DatabaseReadinessProbe(fixture).CheckAsync(canceled.Token));}
    [FunctionalFact]
    public async Task GivenActualTableWait_WhenCallerCancels_ThenDisposeOwningBackend()
    {
        await using var setup=fixture.CreateContext();var name="health_wait_"+Guid.NewGuid().ToString("N");var cs=new NpgsqlConnectionStringBuilder(setup.Database.GetConnectionString()){ApplicationName=name,Pooling=false};
        await using var transaction=await setup.Database.BeginTransactionAsync();await setup.Database.ExecuteSqlRawAsync("LOCK TABLE cerberus.account IN ACCESS EXCLUSIVE MODE");
        using var cancellation=new CancellationTokenSource();var pending=new DatabaseReadinessProbe(new Factory(cs.ConnectionString)).CheckAsync(cancellation.Token);
        await WaitForLock(name);cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);Assert.Equal(0,await Active(name));await transaction.RollbackAsync();
    }
    private async Task WaitForLock(string name)
    {for(var attempt=0;attempt<100;attempt++){await using var c=fixture.CreateContext();await c.Database.OpenConnectionAsync();await using var command=c.Database.GetDbConnection().CreateCommand();command.CommandText="SELECT count(*) FROM pg_stat_activity WHERE application_name=@name AND wait_event_type='Lock'";var p=command.CreateParameter();p.ParameterName="name";p.Value=name;command.Parameters.Add(p);if(Convert.ToInt64(await command.ExecuteScalarAsync())==1)return;await Task.Delay(20);}throw new TimeoutException("Readiness did not enter actual database lock wait");}
    private async Task<long> Active(string name)
    {await using var c=fixture.CreateContext();await c.Database.OpenConnectionAsync();await using var command=c.Database.GetDbConnection().CreateCommand();command.CommandText="SELECT count(*) FROM pg_stat_activity WHERE application_name=@name";var p=command.CreateParameter();p.ParameterName="name";p.Value=name;command.Parameters.Add(p);return Convert.ToInt64(await command.ExecuteScalarAsync());}
    private static async Task Sql(AppDbContext c,string sql){await c.Database.OpenConnectionAsync();await using var command=c.Database.GetDbConnection().CreateCommand();command.CommandText=sql;await command.ExecuteNonQueryAsync();}
    private sealed class Factory(string connection):IDbContextFactory<AppDbContext>
    {public AppDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);}
}
