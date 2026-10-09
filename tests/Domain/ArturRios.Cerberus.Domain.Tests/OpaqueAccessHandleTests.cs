using ArturRios.Cerberus.Domain.Access;

namespace ArturRios.Cerberus.Domain.Tests;

public class OpaqueAccessHandleTests
{
    [UnitFact]
    public void GivenCanonical32ByteHandle_WhenHashing_ThenReturnOnlyItsSha256Verifier()
    {
        Assert.True(OpaqueAccessHandle.TryHash("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", out var verifier));
        Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", verifier);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAé")]
    public void GivenInvalidHandle_WhenHashing_ThenRejectWithoutVerifier(string? token)
    {
        Assert.False(OpaqueAccessHandle.TryHash(token, out var verifier));
        Assert.Empty(verifier);
    }
}
