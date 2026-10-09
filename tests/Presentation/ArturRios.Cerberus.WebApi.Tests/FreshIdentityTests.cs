using System.Security.Claims;
using ArturRios.Cerberus.WebApi.Middleware;

namespace ArturRios.Cerberus.WebApi.Tests;

public class FreshIdentityTests
{
    [UnitTheory]
    [InlineData("1000",1000,true)][InlineData("1000",1059,true)][InlineData("1000",1060,false)]
    [InlineData("1000",999,false)][InlineData(null,1000,false)][InlineData("bad",1000,false)]
    [InlineData("-1",1000,false)][InlineData("9223372036854775807",1000,false)]
    public void GivenSignedIssuedAt_WhenCheckingFreshness_ThenEnforceExclusiveSixtySecondWindow(string? issued,long now,bool expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(issued is null ? [] : new[] { new Claim("iat",issued) }));
        Assert.Equal(expected,FreshIdentity.IsFresh(principal,DateTimeOffset.FromUnixTimeSeconds(now)));
    }
}
