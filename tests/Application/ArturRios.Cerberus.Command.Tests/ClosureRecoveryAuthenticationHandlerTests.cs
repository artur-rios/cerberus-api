using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ArturRios.Cerberus.Command.Authentication;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class ClosureRecoveryAuthenticationHandlerTests
{
    private static readonly Guid Actor=Guid.NewGuid(),Scope=Guid.NewGuid(),Account=Guid.NewGuid();
    private static readonly DateTimeOffset Now=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private const string Secret="fixture-only-signing-key-32-characters";
    [UnitFact]
    public async Task GivenInvalidCredentials_WhenRecovering_ThenRejectBeforeIdentityDependency()
    {
        var handler=Handler(new(MockBehavior.Strict),new(MockBehavior.Strict));var result=await handler.HandleAsync(new ClosureRecoveryLoginCommand{Email="invalid",Password=""});
        Assert.Contains("validation_failed",result.Errors);Assert.Null(result.Data);
    }
    [UnitFact]
    public async Task GivenMfaChallenge_WhenRecovering_ThenReturnOnlyIdentityChallengeWithoutAccountOrVaultAccess()
    {
        var identity=new Mock<IHeimdallClient>(MockBehavior.Strict);identity.Setup(x=>x.AuthenticateAsync("owner@example.test","password",It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeimdallAuthentication(new(null,null,null,true,"challenge",["App"])));
        var result=await Handler(identity,new(MockBehavior.Strict)).HandleAsync(Login());Assert.True(result.Success);Assert.Null(result.Data!.Account);
        Assert.True(result.Data.Identity.RequiresTwoFactor);Assert.Null(result.Data.Identity.Token);
    }
    [UnitTheory][InlineData(false)][InlineData(true)]
    public async Task GivenFreshHumanAndPendingClosure_WhenAuthenticating_ThenReturnExistingHeimdallTokenAndMinimalRecoveryMetadata(bool mfa)
    {
        var token=Token();var identity=new Mock<IHeimdallClient>(MockBehavior.Strict);var authentication=new HeimdallAuthentication(new(token,Now.AddMinutes(5),true,false,null,null),Actor);
        if(mfa)identity.Setup(x=>x.VerifyChallengeAsync("challenge","123456",null,It.IsAny<CancellationToken>())).ReturnsAsync(authentication);
        else identity.Setup(x=>x.AuthenticateAsync("owner@example.test","password",It.IsAny<CancellationToken>())).ReturnsAsync(authentication);
        var store=new Mock<IAccountClosureRecoveryStore>(MockBehavior.Strict);store.Setup(x=>x.ReadAsync(It.Is<AccountClosureRecoveryRequest>(r=>r.Actor==Actor&&r.AuthenticationIssuedAt==Now.ToUnixTimeSeconds()),It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountClosureRecoveryResult(new(Account,2,Now.AddDays(29),Now)));
        var handler=Handler(identity,store);var result=mfa?await handler.HandleAsync(new ClosureRecoveryVerifyChallengeCommand{ChallengeToken="challenge",Code="123456"}):await handler.HandleAsync(Login());
        Assert.True(result.Success);Assert.Equal(token,result.Data!.Identity.Token);Assert.Equal(Account,result.Data.Account!.AccountId);Assert.Equal(2,result.Data.Account.Revision);
        Assert.DoesNotContain(result.Data.GetType().GetProperties(),x=>x.Name.Contains("Vault")||x.Name.Contains("Envelope"));
    }
    [UnitTheory][InlineData("authentication_required")][InlineData("authentication_forbidden")][InlineData("identity_unavailable")][InlineData("private-failure")]
    public async Task GivenIdentityDenialOrDependencyFailure_WhenRecovering_ThenReturnOnlySafeError(string error)
    {
        var identity=new Mock<IHeimdallClient>(MockBehavior.Strict);identity.Setup(x=>x.AuthenticateAsync("owner@example.test","password",It.IsAny<CancellationToken>())).ReturnsAsync(new HeimdallAuthentication(Error:error));
        var result=await Handler(identity,new(MockBehavior.Strict)).HandleAsync(Login());Assert.Contains(error=="private-failure"?"identity_unavailable":error,result.Errors);Assert.Null(result.Data);
    }
    [UnitTheory][InlineData("old")][InlineData("future")][InlineData("missing")][InlineData("duplicate")]
    [InlineData("scope")][InlineData("role")][InlineData("actor")][InlineData("signature")]
    public async Task GivenInvalidSignedRecoveryIdentity_WhenAuthenticating_ThenNeverReadClosure(string kind)
    {
        var identity=new Mock<IHeimdallClient>(MockBehavior.Strict);identity.Setup(x=>x.AuthenticateAsync("owner@example.test","password",It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeimdallAuthentication(new(Token(kind),Now.AddMinutes(5),true,false,null,null),Actor));
        var result=await Handler(identity,new(MockBehavior.Strict)).HandleAsync(Login());Assert.False(result.Success);Assert.Null(result.Data);
        Assert.Contains(kind is "old" or "future" or "missing"?"fresh_authentication_required":"authentication_required",result.Errors);
    }
    [UnitTheory][InlineData("authentication_required")][InlineData("closure_expired")][InlineData("persistence_unavailable")][InlineData("private-failure")]
    public async Task GivenUnavailableOrNonPendingAccount_WhenRecovering_ThenReturnNoIdentityToken(string error)
    {
        var identity=new Mock<IHeimdallClient>(MockBehavior.Strict);identity.Setup(x=>x.AuthenticateAsync("owner@example.test","password",It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeimdallAuthentication(new(Token(),Now.AddMinutes(5),true,false,null,null),Actor));
        var store=new Mock<IAccountClosureRecoveryStore>(MockBehavior.Strict);store.Setup(x=>x.ReadAsync(It.IsAny<AccountClosureRecoveryRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new AccountClosureRecoveryResult(Error:error));
        var result=await Handler(identity,store).HandleAsync(Login());Assert.Contains(error=="private-failure"?"persistence_unavailable":error,result.Errors);Assert.Null(result.Data);
    }
    private static ClosureRecoveryLoginCommand Login()=>new(){Email="owner@example.test",Password="password"};
    private static ClosureRecoveryAuthenticationHandler Handler(Mock<IHeimdallClient> identity,Mock<IAccountClosureRecoveryStore> store)=>new(
        new LoginValidator(),new VerifyChallengeValidator(),identity.Object,store.Object,new HeimdallTokenValidator(new CerberusOptions{
            HeimdallScopeId=Scope,AuthIssuer="fixture-issuer",AuthAudience="fixture-audience",AuthValidationSecret=Secret}),new Clock());
    private static string Token(string kind="")
    {
        var claims=new List<Claim>{new("id",(kind=="actor"?Guid.NewGuid():Actor).ToString()),new("scopeId",(kind=="scope"?Guid.NewGuid():Scope).ToString()),new("roleId",kind=="role"?"2":"3")};
        if(kind!="missing")claims.Add(new("iat",(Now.ToUnixTimeSeconds()+(kind=="old"?-60:kind=="future"?1:0)).ToString(),ClaimValueTypes.Integer64));
        if(kind=="duplicate")claims.Add(new("iat",Now.ToUnixTimeSeconds().ToString(),ClaimValueTypes.Integer64));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("fixture-issuer","fixture-audience",claims,DateTime.UtcNow.AddMinutes(-5),DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes(kind=="signature"?Secret+"forged":Secret)),SecurityAlgorithms.HmacSha256)));
    }
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
}
