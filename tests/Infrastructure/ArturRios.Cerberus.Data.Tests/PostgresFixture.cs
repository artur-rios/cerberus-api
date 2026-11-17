using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(ArturRios.Cerberus.Data.Tests.PostgresServer))]

namespace ArturRios.Cerberus.Data.Tests;

// One PostgreSQL container for the whole assembly. It holds a migrated template database, and every PostgresFixture
// is a database of its own copied from it: test classes no longer share rows, a backup restore rolls back only its own
// class's database, and the classes can run in parallel. Durability is switched off because nothing here survives the
// container; a clean stop still flushes everything.
public sealed class PostgresServer : IAsyncLifetime
{
    private const string Template = "cerberus_template";
    private static PostgresServer? _current;
    private readonly SemaphoreSlim _create = new(1, 1);
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6-alpine")
        .WithCommand("-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off", "-c", "max_connections=500")
        .Build();

    internal static PostgresServer Current => _current ?? throw new InvalidOperationException("The PostgreSQL server fixture has not started.");

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await ExecuteAsync($"CREATE DATABASE \"{Template}\"");
        await using (var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                         .UseNpgsql(ConnectionString(Template, pooling: false)).Options))
            await context.Database.MigrateAsync();
        await CalibrateClockAsync();
        _current = this;
    }

    // With every class running at once a new connection can take longer than Npgsql's 15-second default to open (through
    // the container's mapped port), so the connect timeout is longer. It bounds opening a connection only, not a command.
    internal string ConnectionString(string database, bool pooling = true) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database, Pooling = pooling, Timeout = 60 }.ConnectionString;

    // A copy of the migrated template, or an empty database for the migration tests that start from an older schema.
    internal async Task<string> CreateDatabaseAsync(bool migrated = true)
    {
        var name = "cerberus_" + Guid.NewGuid().ToString("N");
        // A copy needs the template to be idle, so copies are made one at a time.
        await _create.WaitAsync();
        try { await ExecuteAsync($"CREATE DATABASE \"{name}\"" + (migrated ? $" TEMPLATE \"{Template}\"" : "")); }
        finally { _create.Release(); }
        return name;
    }

    internal async Task DropDatabaseAsync(string name)
    {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString(name)));
        await ExecuteAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
    }

    internal Task<DotNet.Testcontainers.Containers.ExecResult> ExecAsync(IList<string> command) => _container.ExecAsync(command);

    // The tests that wait for a bound to pass compare it with the database clock (RealTime).
    private async Task CalibrateClockAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);
        await command.ExecuteScalarAsync();
        var before = DateTimeOffset.UtcNow;
        var now = (DateTime)(await command.ExecuteScalarAsync())!;
        RealTime.Calibrate(new DateTimeOffset(now, TimeSpan.Zero), before, DateTimeOffset.UtcNow);
    }

    private async Task ExecuteAsync(string sql)
    {
        // Pooled: every class creates and drops a database through it, and reusing connections keeps that cheap.
        await using var connection = new NpgsqlConnection(ConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _current = null;
        await _container.DisposeAsync();
        _create.Dispose();
    }
}

// A database of its own for one test class (IClassFixture), or for one test that builds it by hand.
public sealed class PostgresFixture : IAsyncLifetime, IDbContextFactory<AppDbContext>
{
    private string? _database;
    private string Database => _database ?? throw new InvalidOperationException("The PostgreSQL fixture has not been initialized.");
    public string ConnectionString => PostgresServer.Current.ConnectionString(Database);
    public AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);
    public AppDbContext CreateDbContext() => CreateContext();

    // An empty database (no schema) for a test that migrates from an older version itself; dropped with this fixture.
    private readonly List<string> _empty = [];
    public async Task<string> EmptyDatabaseAsync()
    {
        var name = await PostgresServer.Current.CreateDatabaseAsync(migrated: false);
        _empty.Add(name);
        return PostgresServer.Current.ConnectionString(name, pooling: false);
    }

    public async Task<string> BackupAsync()
    {
        var path = "/tmp/cerberus-test-backup-" + Guid.NewGuid().ToString("N") + ".sql";
        var result = await PostgresServer.Current.ExecAsync(["pg_dump", "--username=postgres", "--dbname=" + Database, "--schema=cerberus", "--clean", "--if-exists", "--file=" + path]);
        Assert.True(result.ExitCode == 0, result.Stderr);
        return path;
    }
    public async Task RestoreAsync(string path)
    {
        var result = await PostgresServer.Current.ExecAsync(["psql", "--username=postgres", "--dbname=" + Database, "--set=ON_ERROR_STOP=1", "--file=" + path]);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }
    public async ValueTask InitializeAsync() => _database = await PostgresServer.Current.CreateDatabaseAsync();
    public async ValueTask DisposeAsync()
    {
        foreach (var name in _empty) await PostgresServer.Current.DropDatabaseAsync(name);
        if (_database is not null) await PostgresServer.Current.DropDatabaseAsync(_database);
    }
}
