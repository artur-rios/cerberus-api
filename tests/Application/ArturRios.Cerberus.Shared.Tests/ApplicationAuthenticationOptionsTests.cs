using ArturRios.Cerberus.Shared.Configuration;

namespace ArturRios.Cerberus.Shared.Tests;

public class ApplicationAuthenticationOptionsTests
{
    [UnitTheory][InlineData(null)][InlineData("")][InlineData(" ")][InlineData("cerberus")]
    public void GivenMissingOrPersonAudienceForApplications_WhenValidating_ThenRequireDistinctConfiguredAudience(string? audience)
    {
        var o=ApplicationAuthFixture.Options();o.AuthApplicationAudience=audience;var r=new CerberusOptionsValidator().Validate(null,o);
        Assert.True(r.Failed);Assert.Contains("Invalid or missing CERBERUS_AUTH_APPLICATION_AUDIENCE.",r.Failures!);
    }
    [UnitTheory][InlineData("")][InlineData("short")][InlineData("not-ascii-validation-secret-xxxxxxxx-é")]
    public void GivenInvalidPreviousValidationKey_WhenValidating_ThenRejectWithoutEchoingSecret(string secret)
    {
        var o=ApplicationAuthFixture.Options();o.AuthValidationSecretPrevious=secret;var r=new CerberusOptionsValidator().Validate(null,o);
        Assert.True(r.Failed);Assert.Contains("Invalid or missing CERBERUS_AUTH_VALIDATION_SECRET_PREVIOUS.",r.Failures!);if(secret.Length>0)Assert.DoesNotContain(secret,string.Join(';',r.Failures!),StringComparison.Ordinal);
    }
    [UnitFact]
    public void GivenRegistrationFingerprintKeyReusesPreviousSigningKey_WhenValidating_ThenRejectCrossPurposeReuse()
    {
        var o=ApplicationAuthFixture.Options();o.RegistrationFingerprintKey=o.AuthValidationSecretPrevious;var r=new CerberusOptionsValidator().Validate(null,o);
        Assert.True(r.Failed);Assert.Contains("Invalid or missing CERBERUS_REGISTRATION_FINGERPRINT_KEY.",r.Failures!);
    }
    [UnitTheory][InlineData(false)][InlineData(true)]
    public void GivenDistinctApplicationAudienceAndOptionalValidRotationKey_WhenValidating_ThenAccept(bool previous)
    {
        var o=ApplicationAuthFixture.Options();if(!previous)o.AuthValidationSecretPrevious=null;Assert.True(new CerberusOptionsValidator().Validate(null,o).Succeeded);
    }
}
