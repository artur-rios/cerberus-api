using System.Security.Cryptography;
using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Offline;

namespace ArturRios.Cerberus.Shared.Tests;

public class OfflineLeaseSignerTests
{
    [UnitTheory]
    [InlineData("absent", true)]
    [InlineData("valid", true)]
    [InlineData("issuerOnly", false)]
    [InlineData("keyOnly", false)]
    [InlineData("invalid", false)]
    [InlineData("public", false)]
    [InlineData("wrongCurve", false)]
    [InlineData("nonCanonicalIssuer", false)]
    [InlineData("unicodeIssuer", false)]
    public void GivenOptionalDedicatedSigningConfiguration_WhenValidating_ThenAcceptOnlyAbsentOrCompleteValidPair(string state, bool valid)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var options = CerberusOptionsValidatorTests.ValidOptions();
        options.OfflineLeaseIssuer = state is "absent" or "keyOnly" ? null : state == "nonCanonicalIssuer" ? "e\u0301" : state == "unicodeIssuer" ? "é" : "fixture-lease-issuer";
        options.OfflineLeasePrivateKeyPem = state switch
        {
            "absent" or "issuerOnly" => null, "invalid" => "invalid", "public" => key.ExportSubjectPublicKeyInfoPem(),
            "wrongCurve" => wrong.ExportPkcs8PrivateKeyPem(), _ => key.ExportPkcs8PrivateKeyPem()
        };
        var result = new CerberusOptionsValidator().Validate(null, options);
        Assert.Equal(valid, result.Succeeded);
        if (!valid) Assert.DoesNotContain(options.OfflineLeasePrivateKeyPem ?? "empty", string.Join("", result.Failures!));
    }

    [UnitFact]
    public void GivenProvisionedSigner_WhenSigning_ThenReturnNativeLeaseWithoutVaultKeyDependency()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = new CerberusOptions { OfflineLeaseIssuer = "fixture-lease-issuer", OfflineLeasePrivateKeyPem = key.ExportPkcs8PrivateKeyPem() };
        var result = new OfflineLeaseSigner(options).Sign(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1, 1, 1, [], true, TimeSpan.FromHours(24), DateTimeOffset.FromUnixTimeSeconds(1000)));
        Assert.NotNull(result);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(87400), result.ExpiresAt);
    }

    [UnitFact]
    public void GivenUnprovisionedSigner_WhenSigning_ThenFailClosedWithoutGeneratingAKey()
    {
        Assert.Null(new OfflineLeaseSigner(new()).Sign(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1, 1, 1, [], false, null, DateTimeOffset.FromUnixTimeSeconds(1000))));
    }
}
