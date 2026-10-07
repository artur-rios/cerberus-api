using Microsoft.Extensions.Options;
using Npgsql;

namespace ArturRios.Cerberus.Data.Configuration;

public static class PostgresConfiguration
{
    public static ValidateOptionsResult Validate(string? connectionString)
    {
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(connectionString ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(settings.Host)) return ValidateOptionsResult.Success;
        }
        catch (ArgumentException) { } // Parser messages can contain credentials or unsupported values.
        return ValidateOptionsResult.Fail("Invalid or missing CERBERUS_DATA_CONNECTIONSTRING.");
    }
}
