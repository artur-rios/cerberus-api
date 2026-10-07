using ArturRios.Configuration.Enums;
using ArturRios.Util.Test.Functional;
using ArturRios.Cerberus.WebApi;
using Microsoft.Extensions.Options;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Foundation host")]
public class FoundationHostTests : WebApiTest<Program>
{
    public FoundationHostTests() : base(TestEnvironment.Configure()) { }

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
                ["CERBERUS_BACKUP_RETENTION"] = "7.00:00:00",
                ["CERBERUS_LOG_RETENTION"] = "7.00:00:00",
                ["CERBERUS_SYNC_RETENTION"] = "30.00:00:00",
                ["CERBERUS_RETENTION_INTERVAL"] = "00:05:00",
                ["CERBERUS_BACKUP_PATH"] = "/srv/cerberus/backups",
                ["CERBERUS_ERASURE_LEDGER_PATH"] = _ledger,
                ["CERBERUS_MAX_REQUEST_BYTES"] = "1024",
                ["CERBERUS_MAX_PAGE_SIZE"] = "100"
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
