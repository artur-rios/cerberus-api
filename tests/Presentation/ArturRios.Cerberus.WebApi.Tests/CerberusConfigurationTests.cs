using ArturRios.Cerberus.WebApi.Configuration;
using Microsoft.Extensions.Configuration;

namespace ArturRios.Cerberus.WebApi.Tests;

public class CerberusConfigurationTests
{
    [UnitTheory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("invalid", null)]
    public void GivenRestoreFlag_WhenLoading_ThenBindWithoutSilentlyDisabling(string value, bool? expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CERBERUS_RESTORE_REQUIRED"] = value
        }).Build();
        Assert.Equal(expected, CerberusConfiguration.Load(configuration).RestoreRequired);
    }
    [UnitFact]
    public void GivenEnvironmentSecretAndCheckedInValue_WhenLoading_ThenEnvironmentWins()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cerberus:AuthValidationSecret"] = "checked-in-placeholder",
            ["CERBERUS_AUTH_VALIDATION_SECRET"] = "environment-secret-value",
            ["Cerberus:RegistrationFingerprintKey"] = "checked-in-placeholder",
            ["CERBERUS_REGISTRATION_FINGERPRINT_KEY"] = "protected-durable-key-value"
        }).Build();
        Assert.Equal("environment-secret-value", CerberusConfiguration.Load(configuration).AuthValidationSecret);
        Assert.Equal("protected-durable-key-value", CerberusConfiguration.Load(configuration).RegistrationFingerprintKey);
    }
}
