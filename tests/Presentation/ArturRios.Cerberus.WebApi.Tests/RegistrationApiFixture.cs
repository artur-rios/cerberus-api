using System.Collections.Concurrent;
using ArturRios.Cerberus.Data;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Jwt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace ArturRios.Cerberus.WebApi.Tests;

public sealed class RegistrationApiFixture : IAsyncLifetime
{
    public static readonly Guid Scope = Guid.Parse("527a1001-8ef5-4c9b-a565-111111111111");
    private const string Secret = "fixture-only-signing-key-32-characters";
    private readonly string _serviceToken = ServiceToken();
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:18.6-alpine").Build();
    private readonly Dictionary<string, string?> _previous = new();
    private WebApplication _identity = null!;
    private string _ledger = null!;
    private int _identityCalls;
    public int IdentityCalls => Volatile.Read(ref _identityCalls);
    public ConcurrentDictionary<string, User> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    public sealed record User(Guid Id, string Password, bool Mfa = false);

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        await using (var context = Context()) await context.Database.MigrateAsync();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        _identity = builder.Build();
        _identity.Use(async (_, next) =>
        {
            Interlocked.Increment(ref _identityCalls);
            await next();
        });
        _identity.Urls.Add("http://127.0.0.1:0");
        _identity.MapPost("/api/auth/login", (LoginInput input) =>
        {
            if (input.Email.StartsWith("unavailable-", StringComparison.Ordinal)) return Results.StatusCode(503);
            if (input.ScopeId != Scope || !Users.TryGetValue(input.Email, out var user) || input.Password != user.Password)
                return Results.StatusCode(401);
            return user.Mfa
                ? Envelope(new { requiresTwoFactor = true, challengeToken = "fixture-challenge", availableMethods = new[] { "App" } })
                : Envelope(new { requiresTwoFactor = false, token = Token(user.Id), expiresAt = DateTimeOffset.UtcNow.AddMinutes(5), emailVerified = true });
        });
        _identity.MapPost("/api/scopes/{scope:guid}/persons", (Guid scope, HeimdallRegistration input, HttpContext request) =>
        {
            if (scope != Scope || input.Email.StartsWith("missing-", StringComparison.Ordinal)) return Results.StatusCode(404);
            if (input.Email.StartsWith("forbidden-", StringComparison.Ordinal)) return Results.StatusCode(403);
            if (request.Request.Headers.Authorization != "Bearer " + _serviceToken) return Results.StatusCode(403);
            var user = new User(Guid.NewGuid(), input.Password);
            return Users.TryAdd(input.Email, user) ? Person(user) : Results.StatusCode(409);
        });
        _identity.MapGet("/api/persons/{id:guid}", (Guid id) =>
        {
            var user = Users.Values.SingleOrDefault(x => x.Id == id);
            return user is null ? Results.StatusCode(404) : Person(user);
        });
        await _identity.StartAsync();
        var address = _identity.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _ledger = Directory.CreateTempSubdirectory("cerberus-registration-ledger-").FullName;
        var values = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "local",
            ["CERBERUS_DATA_CONNECTIONSTRING"] = _database.GetConnectionString(),
            ["CERBERUS_DATA_DATABASETYPE"] = "PostgreSql",
            ["CERBERUS_HEIMDALL_BASE_URL"] = address,
            ["CERBERUS_HEIMDALL_SCOPE_ID"] = Scope.ToString(),
            ["CERBERUS_HEIMDALL_SERVICE_CREDENTIAL"] = _serviceToken,
            ["CERBERUS_AUTH_ISSUER"] = "fixture-issuer", ["CERBERUS_AUTH_AUDIENCE"] = "fixture-audience",
            ["CERBERUS_AUTH_VALIDATION_SECRET"] = Secret,
            ["CERBERUS_REGISTRATION_FINGERPRINT_KEY"] = "independent-durable-fixture-key-32-bytes",
            ["CERBERUS_BACKUP_RETENTION"] = "7.00:00:00", ["CERBERUS_LOG_RETENTION"] = "7.00:00:00",
            ["CERBERUS_SYNC_RETENTION"] = "30.00:00:00", ["CERBERUS_RETENTION_INTERVAL"] = "00:05:00",
            ["CERBERUS_BACKUP_PATH"] = "/srv/cerberus/backups", ["CERBERUS_ERASURE_LEDGER_PATH"] = _ledger,
            ["CERBERUS_MAX_REQUEST_BYTES"] = "65536", ["CERBERUS_MAX_PAGE_SIZE"] = "100",
            ["CERBERUS_RESTORE_REQUIRED"] = "false"
        };
        foreach (var (key, value) in values)
        {
            _previous[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_database.GetConnectionString()).Options);
    public Task StopDatabaseAsync() => _database.StopAsync();
    public Task StartDatabaseAsync() => _database.StartAsync();
    public async Task RejectAccountInsertAsync(Guid accountId)
    {
        await using var context = Context();
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE cerberus.fixture_reject_account (public_id uuid NOT NULL); CREATE FUNCTION cerberus.reject_fixture_account() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF EXISTS (SELECT 1 FROM cerberus.fixture_reject_account WHERE public_id = NEW.public_id) THEN RAISE EXCEPTION 'synthetic persistence outage' USING ERRCODE = '08006'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_fixture_account BEFORE INSERT ON cerberus.account FOR EACH ROW EXECUTE FUNCTION cerberus.reject_fixture_account();");
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.fixture_reject_account (public_id) VALUES ({accountId})");
    }
    public async Task AllowAccountInsertAsync()
    {
        await using var context = Context();
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS reject_fixture_account ON cerberus.account; DROP FUNCTION IF EXISTS cerberus.reject_fixture_account(); DROP TABLE IF EXISTS cerberus.fixture_reject_account;");
    }
    public async Task DisposeAsync()
    {
        await _identity.DisposeAsync();
        await _database.DisposeAsync();
        foreach (var (key, value) in _previous) Environment.SetEnvironmentVariable(key, value);
        Directory.Delete(_ledger, recursive: true);
    }
    public static string Token(Guid id) => new JwtHandler().CreateToken(new JwtConfiguration(60, "fixture-issuer", "fixture-audience", Secret,
        new Dictionary<string, string> { ["id"] = id.ToString(), ["roleId"] = "3", ["scopeId"] = Scope.ToString() }));
    private static string ServiceToken() => new JwtHandler().CreateToken(new JwtConfiguration(60, "fixture-issuer", "fixture-audience", Secret,
        new Dictionary<string, string> { ["id"] = Guid.Parse("527a1001-8ef5-4c9b-a565-333333333333").ToString(), ["roleId"] = "2", ["ownedScopeIds"] = Scope.ToString() }));
    private static IResult Person(User user) => Envelope(new { id = user.Id, name = "Fixture Owner", email = "fixture@example.test", role = 3, scopeId = Scope, isDeleted = false, emailVerified = true, twoFactorEnabled = user.Mfa, ownedScopeIds = Array.Empty<Guid>() });
    private static IResult Envelope(object data) => Results.Json(new { success = true, errors = Array.Empty<string>(), messages = Array.Empty<string>(), data });
    private sealed record LoginInput(string Email, string Password, Guid ScopeId);
}

[CollectionDefinition("Registration host", DisableParallelization = true)]
public sealed class RegistrationHostCollection : ICollectionFixture<RegistrationApiFixture>;
