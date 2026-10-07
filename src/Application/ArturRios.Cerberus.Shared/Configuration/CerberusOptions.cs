namespace ArturRios.Cerberus.Shared.Configuration;

public sealed class CerberusOptions
{
    public string? ConnectionString { get; set; }
    public string? DatabaseType { get; set; }
    public string? HeimdallBaseUrl { get; set; }
    public Guid HeimdallScopeId { get; set; }
    public string? HeimdallServiceCredential { get; set; }
    public string? AuthIssuer { get; set; }
    public string? AuthAudience { get; set; }
    public string? AuthValidationSecret { get; set; }
    public string? BackupRetention { get; set; }
    public string? LogRetention { get; set; }
    public string? SyncRetention { get; set; }
    public string? RetentionInterval { get; set; }
    public string? BackupPath { get; set; }
    public string? ErasureLedgerPath { get; set; }
    public long MaxRequestBytes { get; set; }
    public int MaxPageSize { get; set; }
}
