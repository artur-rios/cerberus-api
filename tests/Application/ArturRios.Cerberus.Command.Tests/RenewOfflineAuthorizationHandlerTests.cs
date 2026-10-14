using System.Security.Cryptography;
using ArturRios.Cerberus.Command.Offline;
using ArturRios.Cerberus.Domain.Offline;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class RenewOfflineAuthorizationHandlerTests
{
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid Identity = Guid.NewGuid(), Profile = Guid.NewGuid();
    private static RenewOfflineAuthorizationCommand Command()
    { var c = new RenewOfflineAuthorizationCommand { ProfileId = Profile, ExpectedProfileRevision = 3 }; c.SetContext(Identity, Handle); return c; }
    [UnitTheory]
    [InlineData("identity", "authentication_required")][InlineData("missing", "vault_access_required")]
    [InlineData("handle", "validation_failed")][InlineData("profile", "validation_failed")]
    [InlineData("revision", "validation_failed")][InlineData("unsafeRevision", "validation_failed")]
    public async Task GivenInvalidInput_WhenRenewing_ThenRejectBeforeStorage(string invalid, string error)
    {
        var c = Command();
        c.SetContext(invalid == "identity" ? Guid.Empty : Identity, invalid == "missing" ? null : invalid == "handle" ? "invalid" : Handle);
        if (invalid == "profile") c.ProfileId = Guid.Empty;
        if (invalid == "revision") c.ExpectedProfileRevision = 0;
        if (invalid == "unsafeRevision") c.ExpectedProfileRevision = 9007199254740992;
        var result = await new RenewOfflineAuthorizationHandler(new RenewOfflineAuthorizationValidator(), new Mock<IOfflineAuthorizationStore>(MockBehavior.Strict).Object).HandleAsync(c);
        Assert.Contains(error, result.Errors); Assert.Null(result.Data);
    }
    [UnitTheory]
    [InlineData("not_found")][InlineData("vault_access_denied")][InlineData("revision_conflict")][InlineData("persistence_unavailable")]
    public async Task GivenDeniedRenewal_WhenHandling_ThenReturnStableErrorWithoutLease(string error)
    {
        var result = await Handler(new(Error: error)).HandleAsync(Command());
        Assert.Contains(error, result.Errors); Assert.Null(result.Data);
    }
    [UnitTheory]
    [InlineData(true)][InlineData(false)]
    public async Task GivenCurrentSignedPolicy_WhenHandling_ThenReturnExactScopedPolicy(bool enabled)
    {
        var details = Details(enabled);
        var result = await Handler(new(details)).HandleAsync(Command());
        Assert.True(result.Success);
        Assert.Equal(details.Lease.Compact, result.Data!.Lease);
        Assert.Equal(Profile, result.Data.ProfileId);
        Assert.Equal(3, result.Data.ProtectionRevision);
        Assert.Equal(enabled, result.Data.RenewalEnabled);
        Assert.Equal(details.Lease.ExpiresAt, result.Data.ExpiresAt);
    }
    [UnitTheory]
    [InlineData("empty")][InlineData("identity")][InlineData("profile")][InlineData("revision")]
    [InlineData("policy")][InlineData("lease")][InlineData("expiry")][InlineData("issued")]
    public async Task GivenMismatchedStoredOutcome_WhenHandling_ThenFailClosed(string invalid)
    {
        var d = Details(true);
        d = invalid switch
        {
            "identity" => d with { Snapshot = d.Snapshot with { IdentityId = Guid.NewGuid() } },
            "profile" => d with { Snapshot = d.Snapshot with { ProfileId = Guid.NewGuid() } },
            "revision" => d with { Snapshot = d.Snapshot with { ProtectionRevision = 4 } },
            "policy" => d with { Snapshot = d.Snapshot with { PolicyRevision = 0 } },
            "lease" => d with { Lease = d.Lease with { Compact = "invalid" } },
            "expiry" => d with { Lease = d.Lease with { ExpiresAt = null } },
            "issued" => d with { Lease = d.Lease with { IssuedAt = d.Lease.IssuedAt.AddSeconds(1) } }, _ => d
        };
        var result = await Handler(new(invalid == "empty" ? null : d)).HandleAsync(Command());
        Assert.Contains("persistence_unavailable", result.Errors); Assert.Null(result.Data);
    }
    [UnitFact]
    public async Task GivenCancelledCaller_WhenRenewing_ThenPropagateCancellation()
    {
        using var c = new CancellationTokenSource(); c.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(new()).HandleAsync(Command(), c.Token));
    }
    private static OfflineAuthorizationDetails Details(bool enabled)
    {
        var s = new OfflineLeaseSnapshot(Identity, Guid.NewGuid(), Profile, 2, 3, 1, 1, [], enabled,
            enabled ? TimeSpan.FromHours(24) : null, DateTimeOffset.FromUnixTimeSeconds(1000));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new(s, OfflineLease.Sign(s, "fixture-lease-issuer", key.ExportPkcs8PrivateKeyPem()));
    }
    private static RenewOfflineAuthorizationHandler Handler(OfflineAuthorizationResult result)
    {
        var store = new Mock<IOfflineAuthorizationStore>(MockBehavior.Strict);
        store.Setup(x => x.RenewAsync(It.Is<OfflineAuthorizationRequest>(r => r.IdentityId == Identity && r.ProfileId == Profile
            && r.ExpectedProfileRevision == 3 && r.Verifier == "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925"), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return new(new RenewOfflineAuthorizationValidator(), store.Object);
    }
}
