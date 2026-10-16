using System.Text.Json.Serialization;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;

namespace ArturRios.Cerberus.Shared.Operations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HealthStatus(string Status);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HealthComponent(string Name, string Status, long ElapsedMilliseconds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HealthDetails(string Status, IReadOnlyList<HealthComponent> Components);
public interface IDatabaseReadinessProbe { Task<bool> CheckAsync(CancellationToken ct); }

public sealed class OperationalHealthProbe(IDatabaseReadinessProbe database, IHeimdallClient identity,
    RestoreTrafficGate gate, CerberusOptions options, TimeProvider clock)
{
    public async Task<HealthDetails> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var databaseCheck = Component("database", database.CheckAsync, ct);
        var identityCheck = Component("identity", identity.VerifyScopeAsync, ct);
        var dependencies = await Task.WhenAll(databaseCheck, identityCheck);
        ct.ThrowIfCancellationRequested();
        // Restore may close while either dependency is pending. It is sampled last.
        var restore = new HealthComponent("restore", options.RestoreRequired != true || gate.IsReady ? "healthy" : "unhealthy", 0);
        HealthComponent[] components = [dependencies[0], dependencies[1], restore];
        return new(components.All(x => x.Status == "healthy") ? "healthy" : "unhealthy", components);
    }

    private async Task<HealthComponent> Component(string name, Func<CancellationToken, Task<bool>> check, CancellationToken ct)
    {
        var start = clock.GetTimestamp();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        bool healthy;
        try { healthy = await check(linked.Token) && !budget.IsCancellationRequested; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { healthy = false; }
        ct.ThrowIfCancellationRequested();
        return new(name, healthy ? "healthy" : "unhealthy", Math.Max(0, (long)clock.GetElapsedTime(start).TotalMilliseconds));
    }
}
