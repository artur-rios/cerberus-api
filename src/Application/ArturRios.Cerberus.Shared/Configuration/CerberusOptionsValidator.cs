using Microsoft.Extensions.Options;
using System.Globalization;

namespace ArturRios.Cerberus.Shared.Configuration;

public sealed class CerberusOptionsValidator(bool production = true) : IValidateOptions<CerberusOptions>
{
    public ValidateOptionsResult Validate(string? name, CerberusOptions options)
    {
        var errors = new List<string>();
        void Require(bool valid, string setting)
        {
            if (!valid) errors.Add($"Invalid or missing {setting}.");
        }

        Require(!string.IsNullOrWhiteSpace(options.ConnectionString), "CERBERUS_DATA_CONNECTIONSTRING");
        Require(options.DatabaseType == "PostgreSql", "CERBERUS_DATA_DATABASETYPE");
        Require(Uri.TryCreate(options.HeimdallBaseUrl, UriKind.Absolute, out var endpoint)
                && (endpoint.Scheme == Uri.UriSchemeHttps || (!production && endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp))
                && string.IsNullOrEmpty(endpoint.UserInfo), "CERBERUS_HEIMDALL_BASE_URL");
        Require(options.HeimdallScopeId != Guid.Empty, "CERBERUS_HEIMDALL_SCOPE_ID");
        Require(!string.IsNullOrWhiteSpace(options.HeimdallServiceCredential), "CERBERUS_HEIMDALL_SERVICE_CREDENTIAL");
        Require(!string.IsNullOrWhiteSpace(options.AuthIssuer), "CERBERUS_AUTH_ISSUER");
        Require(!string.IsNullOrWhiteSpace(options.AuthAudience), "CERBERUS_AUTH_AUDIENCE");
        Require(options.AuthValidationSecret is { Length: >= 32 } secret && secret.All(c => c is >= '!' and <= '~'), "CERBERUS_AUTH_VALIDATION_SECRET");
        foreach (var (value, key) in new[]
        {
            (options.BackupRetention, "CERBERUS_BACKUP_RETENTION"),
            (options.LogRetention, "CERBERUS_LOG_RETENTION"),
            (options.SyncRetention, "CERBERUS_SYNC_RETENTION"),
            (options.RetentionInterval, "CERBERUS_RETENTION_INTERVAL")
        })
        {
            Require(TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out var interval) && interval > TimeSpan.Zero
                && (key != "CERBERUS_RETENTION_INTERVAL" || interval <= TimeSpan.FromMilliseconds(uint.MaxValue - 1)),
                key);
        }

        Require(options.BackupPath is not null && Path.IsPathFullyQualified(options.BackupPath), "CERBERUS_BACKUP_PATH");
        Require(options.ErasureLedgerPath is not null && Path.IsPathFullyQualified(options.ErasureLedgerPath)
                && !IsWithin(options.ErasureLedgerPath!, options.BackupPath), "CERBERUS_ERASURE_LEDGER_PATH");
        Require(options.MaxRequestBytes > 0, "CERBERUS_MAX_REQUEST_BYTES");
        Require(options.MaxPageSize > 0, "CERBERUS_MAX_PAGE_SIZE");
        Require(options.RestoreRequired.HasValue, "CERBERUS_RESTORE_REQUIRED");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsWithin(string path, string? parent)
    {
        if (parent is null || !Path.IsPathFullyQualified(parent)) return false;
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath == fullParent || fullPath.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
