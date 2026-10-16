using ArturRios.Cerberus.Shared.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Operations;

public sealed class DatabaseReadinessProbe(IDbContextFactory<AppDbContext> factory) : IDatabaseReadinessProbe
{
    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var context = await factory.CreateDbContextAsync(ct);
            // This fresh context owns its connection. Direct ADO avoids emitting
            // dependency exception text through EF command logging.
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT public_id FROM cerberus.account LIMIT 0";
            command.CommandTimeout = 5;
            await using var reader = await command.ExecuteReaderAsync(ct);
            ct.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { ct.ThrowIfCancellationRequested(); return false; }
    }
}
