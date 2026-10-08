using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Query.Accounts;
using Moq;

namespace ArturRios.Cerberus.Query.Tests;

public class GetAccountHandlerTests
{
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid Identity = Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [UnitTheory]
    [InlineData("identity", "authentication_required")]
    [InlineData("missing", "vault_access_required")]
    [InlineData("malformed", "validation_failed")]
    public async Task GivenInvalidContext_WhenReadingAccount_ThenRejectBeforePersistence(string invalid, string error)
    {
        var query = new GetAccountQuery(invalid == "identity" ? Guid.Empty : Identity, invalid == "missing" ? null : invalid == "malformed" ? "invalid" : Handle);
        var result = await new GetAccountHandler(new Mock<IAccountReadStore>(MockBehavior.Strict).Object, new Clock()).HandleAsync(query);
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
    }

    [UnitTheory]
    [InlineData("not_found")]
    [InlineData("vault_access_denied")]
    [InlineData("persistence_unavailable")]
    public async Task GivenDeniedOrUnavailableRead_WhenHandling_ThenReturnNoCiphertext(string error)
    {
        var result = await Handler(new AccountReadResult(Error: error)).HandleAsync(new GetAccountQuery(Identity, Handle));
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
    }

    [UnitFact]
    public async Task GivenAuthorizedOpaqueAccount_WhenHandling_ThenReturnPublicContextAndExactEnvelope()
    {
        var envelope = new EncryptedEnvelope("cerberus-content-v1", 1, Handle, "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA");
        var id = Guid.NewGuid();
        var snapshot = new AccountSnapshot(id, 3, AccountState.Active, JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var result = await Handler(new AccountReadResult(snapshot)).HandleAsync(new GetAccountQuery(Identity, Handle));
        Assert.True(result.Success);
        Assert.Equal(id, result.Data!.Id);
        Assert.Equal(3, result.Data.Revision);
        Assert.Equal("active", result.Data.State);
        Assert.Equal(envelope, result.Data.Details);
    }

    [UnitTheory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"format\":\"unknown\"}")]
    [InlineData("{\"format\":\"cerberus-content-v1\",\"keyEpoch\":\"1\"}")]
    public async Task GivenCorruptStoredEnvelope_WhenHandling_ThenReturnUnavailableWithoutCiphertext(string json)
    {
        var snapshot = new AccountSnapshot(Guid.NewGuid(), 1, AccountState.Active, System.Text.Encoding.UTF8.GetBytes(json));
        var result = await Handler(new AccountReadResult(snapshot)).HandleAsync(new GetAccountQuery(Identity, Handle));
        Assert.Contains("persistence_unavailable", result.Errors);
        Assert.Null(result.Data);
    }

    [UnitFact]
    public async Task GivenIncompleteStoreResult_WhenHandling_ThenFailClosed()
    {
        var result = await Handler(new AccountReadResult()).HandleAsync(new GetAccountQuery(Identity, Handle));
        Assert.Contains("persistence_unavailable", result.Errors);
    }

    private static GetAccountHandler Handler(AccountReadResult result)
    {
        var store = new Mock<IAccountReadStore>(MockBehavior.Strict);
        store.Setup(x => x.ReadAsync(Identity, "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", Now, It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return new(store.Object, new Clock());
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
