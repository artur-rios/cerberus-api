using ArturRios.Cerberus.WebApi.Configuration;
using Microsoft.Extensions.Configuration;

namespace ArturRios.Cerberus.WebApi.Tests;

public class CerberusConfigurationTests
{
    [UnitFact]
    public void GivenEnvironmentSecretAndCheckedInValue_WhenLoading_ThenEnvironmentWins()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cerberus:AuthValidationSecret"] = "checked-in-placeholder",
            ["CERBERUS_AUTH_VALIDATION_SECRET"] = "environment-secret-value"
        }).Build();
        Assert.Equal("environment-secret-value", CerberusConfiguration.Load(configuration).AuthValidationSecret);
    }
}
