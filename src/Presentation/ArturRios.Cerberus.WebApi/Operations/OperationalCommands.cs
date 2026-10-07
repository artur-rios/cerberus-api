using ArturRios.Cerberus.Data;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Operations;

public static class OperationalCommands
{
    public static async Task<int> ExecuteAsync(IServiceProvider services, string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = services.CreateScope();
        switch (command)
        {
            case "--validate-configuration": return 0; // Startup already validated settings and ledger storage.
            case "--migrate":
                await using (var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(cancellationToken))
                    await context.Database.MigrateAsync(cancellationToken);
                return 0;
            case "--reconcile-restore":
                return await scope.ServiceProvider.GetRequiredService<IRestoreReconciler>().ReconcileAsync(cancellationToken) ? 0 : 1;
            default: throw new ArgumentException("Unsupported maintenance command.");
        }
    }

    public static Task<int> PrepareTrafficAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<CerberusOptions>().RestoreRequired == true
            ? ExecuteAsync(services, "--reconcile-restore", cancellationToken)
            : Task.FromResult(0);
}
