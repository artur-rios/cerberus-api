using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.WebApi.Operations;
using Microsoft.Extensions.DependencyInjection;
using ArturRios.Cerberus.Shared.Configuration;

namespace ArturRios.Cerberus.WebApi.Tests;

public sealed class OperationalCommandsTests
{
    [UnitTheory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task GivenRestoreCommand_WhenExecuting_ThenRequireSuccessfulReconciliation(bool ready, int expected)
    {
        var reconciliation = new Reconciliation(ready);
        using var services = new ServiceCollection().AddSingleton<IRestoreReconciler>(reconciliation).BuildServiceProvider();
        Assert.Equal(expected, await OperationalCommands.ExecuteAsync(services, "--reconcile-restore", default));
        Assert.Equal(1, reconciliation.Calls);
    }

    [UnitFact]
    public async Task GivenUnknownMaintenanceCommand_WhenExecuting_ThenRejectRatherThanStartTraffic()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await Assert.ThrowsAsync<ArgumentException>(() => OperationalCommands.ExecuteAsync(services, "--unknown-maintenance", default));
    }

    private sealed class Reconciliation(bool ready) : IRestoreReconciler
    {
        public int Calls { get; private set; }
        public Task<bool> ReconcileAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult(ready); }
    }

    [UnitTheory]
    [InlineData(true, true, 0, 1)]
    [InlineData(true, false, 1, 1)]
    [InlineData(false, false, 0, 0)]
    public async Task GivenStartupRestoreMode_WhenPreparingTraffic_ThenReconcileBeforeRunning(bool required, bool ready, int expected, int calls)
    {
        var reconciliation = new Reconciliation(ready);
        using var services = new ServiceCollection().AddSingleton(new CerberusOptions { RestoreRequired = required })
            .AddSingleton<IRestoreReconciler>(reconciliation).BuildServiceProvider();
        Assert.Equal(expected, await OperationalCommands.PrepareTrafficAsync(services, default));
        Assert.Equal(calls, reconciliation.Calls);
    }
}
