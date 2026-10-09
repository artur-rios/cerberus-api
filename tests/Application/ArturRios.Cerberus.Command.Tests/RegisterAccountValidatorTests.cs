using System.Text.Json;
using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Identity;

namespace ArturRios.Cerberus.Command.Tests;

public class RegisterAccountValidatorTests
{
    [UnitFact]
    public void GivenValidRegistration_WhenValidating_ThenAccept()
    {
        Assert.True(new RegisterAccountValidator().Validate(Valid()).IsValid);
    }

    [UnitTheory]
    [InlineData("account")]
    [InlineData("operation")]
    [InlineData("identity")]
    [InlineData("details")]
    [InlineData("name")]
    [InlineData("longName")]
    [InlineData("email")]
    [InlineData("password")]
    [InlineData("format")]
    public void GivenInvalidVisibleInput_WhenValidating_ThenRejectWithStableCode(string field)
    {
        var command = Valid();
        switch (field)
        {
            case "account": command.AccountId = Guid.Empty; break;
            case "operation": command.IdempotencyKey = Guid.Empty; break;
            case "identity": command.Identity = null!; break;
            case "details": command.Details = null!; break;
            case "name": command.Identity = command.Identity with { Name = " " }; break;
            case "longName": command.Identity = command.Identity with { Name = new string('x', 201) }; break;
            case "email": command.Identity = command.Identity with { Email = "not-an-email" }; break;
            case "password": command.Identity = command.Identity with { Password = "short" }; break;
            case "format": command.Details = command.Details with { Format = "unsupported" }; break;
        }
        var result = new RegisterAccountValidator().Validate(command);
        Assert.False(result.IsValid);
        Assert.All(result.Errors, error => Assert.Equal("validation_failed", error.ErrorMessage));
    }

    [UnitFact]
    public void GivenCallerSelectedIdentityId_WhenDeserializing_ThenReject()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegisterAccountCommand>(
            "{\"heimdallPublicId\":\"527a1001-8ef5-4c9b-a565-222222222222\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    internal static RegisterAccountCommand Valid() => new()
    {
        AccountId = Guid.NewGuid(), IdempotencyKey = Guid.NewGuid(),
        Identity = new HeimdallRegistration("Fixture Owner", "fixture@example.test", "fixture-password"),
        Details = new EncryptedEnvelope("cerberus-content-v1", 1, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA")
    };
}
