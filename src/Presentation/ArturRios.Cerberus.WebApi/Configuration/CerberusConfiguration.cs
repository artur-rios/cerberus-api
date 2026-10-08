using ArturRios.Cerberus.Shared.Configuration;
using System.Globalization;

namespace ArturRios.Cerberus.WebApi.Configuration;

public static class CerberusConfiguration
{
    public static CerberusOptions Load(IConfiguration configuration)
    {
        string? Value(string key, string property) => configuration[key] ?? configuration[$"Cerberus:{property}"];
        var restore = Value("CERBERUS_RESTORE_REQUIRED", nameof(CerberusOptions.RestoreRequired));
        return new CerberusOptions
        {
            ConnectionString = Value("CERBERUS_DATA_CONNECTIONSTRING", nameof(CerberusOptions.ConnectionString)),
            DatabaseType = Value("CERBERUS_DATA_DATABASETYPE", nameof(CerberusOptions.DatabaseType)),
            HeimdallBaseUrl = Value("CERBERUS_HEIMDALL_BASE_URL", nameof(CerberusOptions.HeimdallBaseUrl)),
            HeimdallScopeId = Guid.TryParse(Value("CERBERUS_HEIMDALL_SCOPE_ID", nameof(CerberusOptions.HeimdallScopeId)), out var scope) ? scope : Guid.Empty,
            HeimdallServiceCredential = Value("CERBERUS_HEIMDALL_SERVICE_CREDENTIAL", nameof(CerberusOptions.HeimdallServiceCredential)),
            AuthIssuer = Value("CERBERUS_AUTH_ISSUER", nameof(CerberusOptions.AuthIssuer)),
            AuthAudience = Value("CERBERUS_AUTH_AUDIENCE", nameof(CerberusOptions.AuthAudience)),
            AuthValidationSecret = Value("CERBERUS_AUTH_VALIDATION_SECRET", nameof(CerberusOptions.AuthValidationSecret)),
            RegistrationFingerprintKey = Value("CERBERUS_REGISTRATION_FINGERPRINT_KEY", nameof(CerberusOptions.RegistrationFingerprintKey)),
            BackupRetention = Value("CERBERUS_BACKUP_RETENTION", nameof(CerberusOptions.BackupRetention)),
            LogRetention = Value("CERBERUS_LOG_RETENTION", nameof(CerberusOptions.LogRetention)),
            SyncRetention = Value("CERBERUS_SYNC_RETENTION", nameof(CerberusOptions.SyncRetention)),
            RetentionInterval = Value("CERBERUS_RETENTION_INTERVAL", nameof(CerberusOptions.RetentionInterval)),
            BackupPath = Value("CERBERUS_BACKUP_PATH", nameof(CerberusOptions.BackupPath)),
            ErasureLedgerPath = Value("CERBERUS_ERASURE_LEDGER_PATH", nameof(CerberusOptions.ErasureLedgerPath)),
            MaxRequestBytes = long.TryParse(Value("CERBERUS_MAX_REQUEST_BYTES", nameof(CerberusOptions.MaxRequestBytes)), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : 0,
            MaxPageSize = int.TryParse(Value("CERBERUS_MAX_PAGE_SIZE", nameof(CerberusOptions.MaxPageSize)), NumberStyles.None, CultureInfo.InvariantCulture, out var page) ? page : 0,
            RestoreRequired = restore is null ? false : bool.TryParse(restore, out var required) ? required : null
        };
    }
}
