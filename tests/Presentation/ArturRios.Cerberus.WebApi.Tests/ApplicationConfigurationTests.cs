using ArturRios.Cerberus.WebApi.Configuration;
using Microsoft.Extensions.Configuration;

namespace ArturRios.Cerberus.WebApi.Tests;

public class ApplicationConfigurationTests
{
    [UnitFact]
    public void GivenProtectedApplicationAudienceAndRotationKey_WhenLoading_ThenEnvironmentNamesOverrideHierarchicalValues()
    {
        var c=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Cerberus:AuthApplicationAudience"]="old-app-audience",["CERBERUS_AUTH_APPLICATION_AUDIENCE"]="configured-app-audience",
            ["Cerberus:AuthValidationSecretPrevious"]="old-key",["CERBERUS_AUTH_VALIDATION_SECRET_PREVIOUS"]="protected-previous-key"
        }).Build();var o=CerberusConfiguration.Load(c);
        Assert.Equal("configured-app-audience",o.AuthApplicationAudience);Assert.Equal("protected-previous-key",o.AuthValidationSecretPrevious);
    }
    [UnitFact]
    public void GivenHierarchicalApplicationSettings_WhenLoading_ThenBindWithoutFallbackToPersonAudience()
    {
        var c=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Cerberus:AuthApplicationAudience"]="configured-app-audience",["Cerberus:AuthValidationSecretPrevious"]="protected-previous-key",["Cerberus:AuthAudience"]="people"
        }).Build();var o=CerberusConfiguration.Load(c);
        Assert.Equal("configured-app-audience",o.AuthApplicationAudience);Assert.Equal("protected-previous-key",o.AuthValidationSecretPrevious);Assert.Equal("people",o.AuthAudience);
    }
}
