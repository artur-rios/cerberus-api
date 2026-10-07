using ArturRios.Cerberus.Shared.Configuration;

namespace ArturRios.Cerberus.Shared.Tests;

public class CerberusOptionsValidatorTests
{
    [UnitFact]
    public void GivenMissingRequiredSettings_WhenValidating_ThenRejectWithSettingNamesOnly()
    {
        var options = new CerberusOptions { ConnectionString = "sensitive-connection", HeimdallServiceCredential = "sensitive-credential" };
        var result = new CerberusOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        var message = string.Join(";", result.Failures!);
        Assert.Contains("CERBERUS_BACKUP_RETENTION", message);
        Assert.Contains("CERBERUS_AUTH_ISSUER", message);
        Assert.DoesNotContain("sensitive-connection", message);
        Assert.DoesNotContain("sensitive-credential", message);
    }

    [UnitTheory]
    [InlineData("00:00:00")]
    [InlineData("-01:00:00")]
    [InlineData("999999999999999999999:00:00")]
    [InlineData("unlimited")]
    public void GivenInvalidRetention_WhenValidating_ThenReject(string retention)
    {
        var options = ValidOptions();
        options.BackupRetention = retention;

        var result = new CerberusOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("CERBERUS_BACKUP_RETENTION"));
    }

    [UnitFact]
    public void GivenCompleteExplicitSettings_WhenValidating_ThenAccept()
    {
        Assert.True(new CerberusOptionsValidator().Validate(null, ValidOptions()).Succeeded);
    }

    [UnitTheory]
    [InlineData("Sqlite")]
    [InlineData("PostgreSql;Password=secret")]
    public void GivenUnsupportedProvider_WhenValidating_ThenRejectWithoutEchoingValue(string provider)
    {
        var options = ValidOptions();
        options.DatabaseType = provider;
        var result = new CerberusOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.DoesNotContain(provider, string.Join(";", result.Failures!));
    }

    [UnitFact]
    public void GivenHttpHeimdallInProduction_WhenValidating_ThenReject()
    {
        var options = ValidOptions();
        options.HeimdallBaseUrl = "http://localhost:1234";
        Assert.True(new CerberusOptionsValidator().Validate(null, options).Failed);
        Assert.True(new CerberusOptionsValidator(production: false).Validate(null, options).Succeeded);
    }

    [UnitTheory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(1024, 0)]
    public void GivenInvalidRequestLimits_WhenValidating_ThenReject(long requestBytes, int pageSize)
    {
        var options = ValidOptions();
        options.MaxRequestBytes = requestBytes;
        options.MaxPageSize = pageSize;
        Assert.True(new CerberusOptionsValidator().Validate(null, options).Failed);
    }

    [UnitTheory]
    [InlineData("relative-ledger")]
    [InlineData("/srv/cerberus/backups/ledger")]
    [InlineData("/srv/cerberus/backups")]
    public void GivenLedgerInsideBackupOrRelativePath_WhenValidating_ThenReject(string ledger)
    {
        var options = ValidOptions();
        options.ErasureLedgerPath = ledger;
        Assert.True(new CerberusOptionsValidator().Validate(null, options).Failed);
    }

    internal static CerberusOptions ValidOptions() => new()
    {
        ConnectionString = "Host=localhost;Database=cerberus_test;Username=test;Password=test",
        DatabaseType = "PostgreSql",
        HeimdallBaseUrl = "https://identity.example.test/",
        HeimdallScopeId = Guid.Parse("527a1001-8ef5-4c9b-a565-111111111111"),
        HeimdallServiceCredential = "test-only-service-credential",
        AuthIssuer = "https://identity.example.test",
        AuthAudience = "cerberus",
        AuthValidationSecret = "test-only-ASCII-secret-at-least-32-bytes",
        BackupRetention = "7.00:00:00",
        LogRetention = "7.00:00:00",
        SyncRetention = "30.00:00:00",
        RetentionInterval = "00:05:00",
        BackupPath = "/srv/cerberus/backups",
        ErasureLedgerPath = "/srv/cerberus/erasure-ledger",
        MaxRequestBytes = 1024 * 1024,
        MaxPageSize = 100
    };

    [UnitFact]
    public void GivenIntervalOutsideTimerRange_WhenValidating_ThenRejectBeforeWorkerStarts()
    {
        var options = ValidOptions();
        options.RetentionInterval = "100.00:00:00";
        Assert.True(new CerberusOptionsValidator().Validate(null, options).Failed);
    }

    [UnitFact]
    public void GivenUnparseableRestoreFlag_WhenValidating_ThenRejectRatherThanBypassReconciliation()
    {
        var options = ValidOptions();
        options.RestoreRequired = null;
        Assert.True(new CerberusOptionsValidator().Validate(null, options).Failed);
    }

    [UnitTheory]
    [InlineData("00:00:00.0000001", false)]
    [InlineData("00:00:00.0009999", false)]
    [InlineData("00:00:00.0010000", true)]
    [InlineData("49.17:02:47.2940000", true)]
    [InlineData("49.17:02:47.2950000", false)]
    public void GivenTimerBoundaryInterval_WhenValidating_ThenMatchSupportedPeriodicTimerRange(string interval, bool valid)
    {
        var options = ValidOptions();
        options.RetentionInterval = interval;
        var result = new CerberusOptionsValidator().Validate(null, options);
        Assert.Equal(valid, result.Succeeded);
        if (!valid) Assert.Equal(["Invalid or missing CERBERUS_RETENTION_INTERVAL."], result.Failures);
    }
}
