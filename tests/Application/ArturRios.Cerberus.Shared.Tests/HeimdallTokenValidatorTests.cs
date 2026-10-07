using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace ArturRios.Cerberus.Shared.Tests;

public class HeimdallTokenValidatorTests
{
    [UnitFact]
    public async Task GivenScopedValidToken_WhenValidating_ThenResolveOnlyThePublicIdentity()
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        var identity = Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");
        var token = new JwtHandler().CreateToken(new JwtConfiguration(60, options.AuthIssuer!, options.AuthAudience!, options.AuthValidationSecret!,
            new Dictionary<string, string> { ["id"] = identity.ToString(), ["scopeId"] = options.HeimdallScopeId.ToString() }));
        var result = await new HeimdallTokenValidator(options).ValidateAsync(token);
        Assert.NotNull(result);
        Assert.Equal(identity.ToString(), result.FindFirst("id")!.Value);
    }

    [UnitTheory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("scope")]
    [InlineData("challenge")]
    [InlineData("expired")]
    [InlineData("internal-id")]
    public async Task GivenUntrustedOrPendingToken_WhenValidating_ThenReject(string mutation)
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        var claims = new Dictionary<string, string>
        {
            ["id"] = mutation == "internal-id" ? "123" : Guid.NewGuid().ToString(),
            ["scopeId"] = mutation == "scope" ? Guid.NewGuid().ToString() : options.HeimdallScopeId.ToString()
        };
        if (mutation == "challenge") claims["mfaPending"] = "true";
        var token = new JwtHandler().CreateToken(new JwtConfiguration(60,
            mutation == "issuer" ? "wrong-issuer" : options.AuthIssuer!,
            mutation == "audience" ? "wrong-audience" : options.AuthAudience!,
            mutation == "signature" ? "wrong-test-only-signature-secret-32bytes" : options.AuthValidationSecret!, claims));
        if (mutation == "expired")
        {
            token = new JwtSecurityTokenHandler().CreateEncodedJwt(new SecurityTokenDescriptor
            {
                Issuer = options.AuthIssuer, Audience = options.AuthAudience,
                Claims = claims.ToDictionary(pair => pair.Key, pair => (object)pair.Value),
                NotBefore = DateTime.UtcNow.AddMinutes(-2), Expires = DateTime.UtcNow.AddMinutes(-1),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes(options.AuthValidationSecret!)), SecurityAlgorithms.HmacSha256)
            });
        }
        Assert.Null(await new HeimdallTokenValidator(options).ValidateAsync(token));
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("a.b.c")]
    [InlineData("not-a-token")]
    public async Task GivenMalformedToken_WhenValidating_ThenRejectWithoutThrowing(string token)
    {
        Assert.Null(await new HeimdallTokenValidator(CerberusOptionsValidatorTests.ValidOptions()).ValidateAsync(token));
    }
}
