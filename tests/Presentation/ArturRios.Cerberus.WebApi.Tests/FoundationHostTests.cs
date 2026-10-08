using ArturRios.Configuration.Enums;
using ArturRios.Util.Test.Functional;
using ArturRios.Cerberus.WebApi;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ArturRios.Cerberus.Data;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.WebApi.Operations;
using Swashbuckle.AspNetCore.Swagger;
using Testcontainers.PostgreSql;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Foundation host")]
public class FoundationHostTests : WebApiTest<Program>
{
    public FoundationHostTests() : base(TestEnvironment.Configure()) { }

    [FunctionalFact]
    public async Task GivenBlankPostgresDatabase_WhenMigratingWithCli_ThenApplyAllMigrationsWithoutStartingTraffic()
    {
        await using var database = new PostgreSqlBuilder("postgres:18.6-alpine").Build();
        await database.StartAsync();
        Assert.Equal(0, await Program.Main(["--migrate", "--environment=local", "--CERBERUS_DATA_CONNECTIONSTRING=" + database.GetConnectionString()]));
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.GetConnectionString()).Options);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Empty(await context.TerminalErasures.ToListAsync());
    }

    [FunctionalFact]
    public async Task GivenConcurrentOversizeRequests_WhenRunningHost_ThenConsistentlyBoundBodiesWithoutCaching()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Gateway.Client.PostAsync("/unknown-resource", new ByteArrayContent(new byte[1025]))));
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.DoesNotContain("1025", await response.Content.ReadAsStringAsync());
            }
        }
    }

    [FunctionalFact]
    public async Task GivenValidProvisionedConfiguration_WhenValidatingWithCli_ThenExitWithoutStartingTraffic()
    {
        Assert.Equal(0, await Program.Main(["--validate-configuration", "--environment=local"]));
    }

    [FunctionalFact]
    public async Task GivenMultipleMaintenanceCommands_WhenStarting_ThenRejectAmbiguousOperation()
    {
        Assert.Equal(1, await Program.Main(["--migrate", "--validate-configuration"]));
    }

    [FunctionalFact]
    public async Task GivenRestoreRequiredWithInvalidServiceAuthority_WhenStarting_ThenExitBeforeOpeningHttp()
    {
        Assert.Equal(1, await Program.Main(["--environment=local", "--CERBERUS_RESTORE_REQUIRED=true"]));
    }

    [FunctionalTheory]
    [InlineData("--CERBERUS_MAX_PAGE_SIZE=invalid")]
    [InlineData("--CERBERUS_DATA_CONNECTIONSTRING=not-a-connection-string")]
    [InlineData("--CERBERUS_DATA_CONNECTIONSTRING=Host=localhost;unsupported-secret=protected-value")]
    [InlineData("--CERBERUS_ERASURE_LEDGER_PATH=/tmp/cerberus-test-not-provisioned-storage")]
    public async Task GivenInvalidSettingsOrMissingLedger_WhenValidatingWithCli_ThenExitFailure(string setting)
    {
        Assert.Equal(1, await Program.Main(["--validate-configuration", "--environment=local", setting]));
    }

    [FunctionalFact]
    public void GivenRunningHost_WhenResolvingOperationalServices_ThenUseProductionRegistrations()
    {
        using var application = Startup.CreateApplication(["--environment=local"]);
        using var scope = application.Services.CreateScope();
        var services = scope.ServiceProvider;
        Assert.IsType<HeimdallClient>(services.GetRequiredService<IHeimdallClient>());
        Assert.NotNull(services.GetRequiredService<IDbContextFactory<AppDbContext>>());
        Assert.NotNull(services.GetRequiredService<IRetentionWorkStore>());
        Assert.NotNull(services.GetRequiredService<ITerminalErasureStore>());
        Assert.IsType<RestoreReconciler>(services.GetRequiredService<IRestoreReconciler>());
        Assert.IsType<RetentionExecutor>(services.GetRequiredService<IRetentionExecutor>());
        Assert.Contains(services.GetServices<IHostedService>(), service => service is RetentionWorker);
        Assert.NotNull(services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1"));
    }

    [FunctionalFact]
    public async Task GivenRunningFoundationHost_WhenRequestingUnknownResource_ThenReturnNoStore()
    {
        var response = await Gateway.Client.GetAsync("/unknown-resource");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [FunctionalTheory]
    [InlineData("Staging")]
    [InlineData("Development")]
    [InlineData("custom-environment")]
    public void GivenNonLocalHost_WhenConfiguringHttpHeimdall_ThenRejectTransport(string environment)
    {
        var exception = Assert.Throws<OptionsValidationException>(() => Startup.CreateApplication(
            ["--environment=" + environment, "--CERBERUS_HEIMDALL_BASE_URL=http://localhost:1234"]));
        Assert.Contains("CERBERUS_HEIMDALL_BASE_URL", exception.Message);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) TestEnvironment.Restore();
    }

    private static class TestEnvironment
    {
        private static readonly Dictionary<string, string?> Previous = new();
        private static string? _ledger;

        public static EnvironmentType Configure()
        {
            _ledger = Directory.CreateTempSubdirectory("cerberus-host-test-").FullName;
            var values = new Dictionary<string, string>
            {
                ["CERBERUS_DATA_CONNECTIONSTRING"] = "Host=localhost;Database=unused;Username=test;Password=test",
                ["CERBERUS_DATA_DATABASETYPE"] = "PostgreSql",
                ["CERBERUS_HEIMDALL_BASE_URL"] = "https://heimdall.example.test",
                ["CERBERUS_HEIMDALL_SCOPE_ID"] = Guid.NewGuid().ToString(),
                ["CERBERUS_HEIMDALL_SERVICE_CREDENTIAL"] = "fixture-only-credential",
                ["CERBERUS_AUTH_ISSUER"] = "fixture-issuer",
                ["CERBERUS_AUTH_AUDIENCE"] = "fixture-audience",
                ["CERBERUS_AUTH_VALIDATION_SECRET"] = "fixture-only-signing-key-32-characters",
                ["CERBERUS_REGISTRATION_FINGERPRINT_KEY"] = "independent-durable-fixture-key-32-bytes",
                ["CERBERUS_BACKUP_RETENTION"] = "7.00:00:00",
                ["CERBERUS_LOG_RETENTION"] = "7.00:00:00",
                ["CERBERUS_SYNC_RETENTION"] = "30.00:00:00",
                ["CERBERUS_RETENTION_INTERVAL"] = "00:05:00",
                ["CERBERUS_BACKUP_PATH"] = "/srv/cerberus/backups",
                ["CERBERUS_ERASURE_LEDGER_PATH"] = _ledger,
                ["CERBERUS_MAX_REQUEST_BYTES"] = "1024",
                ["CERBERUS_MAX_PAGE_SIZE"] = "100",
                ["CERBERUS_RESTORE_REQUIRED"] = "false"
            };
            Previous["ASPNETCORE_ENVIRONMENT"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            foreach (var (key, value) in values)
            {
                Previous[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
            return EnvironmentType.Local;
        }

        public static void Restore()
        {
            foreach (var (key, value) in Previous) Environment.SetEnvironmentVariable(key, value);
            Previous.Clear();
            if (_ledger is not null && Directory.Exists(_ledger)) Directory.Delete(_ledger, recursive: true);
        }
    }
}

[CollectionDefinition("Foundation host", DisableParallelization = true)]
public sealed class FoundationHostCollection;
