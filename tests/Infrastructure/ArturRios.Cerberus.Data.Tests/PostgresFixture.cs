using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace ArturRios.Cerberus.Data.Tests;

public sealed class PostgresFixture : IAsyncLifetime, IDbContextFactory<AppDbContext>
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6-alpine").Build();
    public AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_container.GetConnectionString()).Options);
    public AppDbContext CreateDbContext() => CreateContext();
    public async Task<string> BackupAsync()
    {
        var path = "/tmp/cerberus-test-backup-" + Guid.NewGuid().ToString("N") + ".sql";
        var result = await _container.ExecAsync(["pg_dump", "--username=postgres", "--dbname=postgres", "--schema=cerberus", "--clean", "--if-exists", "--file=" + path]);
        Assert.Equal(0, result.ExitCode);
        return path;
    }
    public async Task RestoreAsync(string path)
    {
        var result = await _container.ExecAsync(["psql", "--username=postgres", "--dbname=postgres", "--set=ON_ERROR_STOP=1", "--file=" + path]);
        Assert.Equal(0, result.ExitCode);
    }
    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
