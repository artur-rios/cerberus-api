using System.Net;
using System.Text;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

public partial class AccountDeletionHttpTests
{
    [FunctionalTheory][InlineData("{}")][InlineData("null")][InlineData("[]")][InlineData("{")]
    [InlineData("{\"expectedRevision\":1,\"expectedRevision\":1}")][InlineData("{\"expectedRevision\":1,\"owner\":\"forged\"}")]
    [InlineData("{\"ExpectedRevision\":1}")][InlineData("{\"expectedRevision\":\"1\"}")][InlineData("{\"expectedRevision\":1.0}")]
    [InlineData("{\"expectedRevision\":1e0}")][InlineData("{\"expectedRevision\":null}")][InlineData("{\"expectedRevision\":0}")]
    [InlineData("{\"expectedRevision\":-1}")][InlineData("{\"expectedRevision\":9007199254740992}")]
    public async Task GivenInvalidPermanentDeletionInput_WhenDeleting_ThenRejectWithoutAnyIntent(string body)
    {
        var s=await Setup();var before=await Snapshot(s);using var request=Request(s,body);using var response=await Gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Contains("validation_failed",await response.Content.ReadAsStringAsync());Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("query",400)][InlineData("duplicate",400)][InlineData("empty",400)]
    [InlineData("oversized",413)][InlineData("noMedia",400)][InlineData("text",400)][InlineData("parameter",400)]
    [InlineData("charset",400)][InlineData("duplicateCharset",400)][InlineData("compression",400)][InlineData("bom",400)][InlineData("utf8",400)]
    [InlineData("noToken",401)][InlineData("badToken",401)][InlineData("scope",401)][InlineData("deletedIdentity",401)][InlineData("identityUnavailable",503)]
    [InlineData("missing",404)][InlineData("revoked",403)][InlineData("expired",403)][InlineData("selected",403)]
    [InlineData("noAccess",403)][InlineData("badAccess",400)][InlineData("revision",409)]
    public async Task GivenInvalidTransportOrCurrentAuthority_WhenDeleting_ThenKeepAccountAndReturnSafeStatus(string kind,int status)
    {
        var s=await Setup(kind);var before=await Snapshot(s);using var request=Request(s,kind=="revision"?"{\"expectedRevision\":2}":"{\"expectedRevision\":1}");
        if(kind=="query")request.RequestUri=new("/api/accounts/me/permanent?owner=forged",UriKind.Relative);
        if(kind=="duplicate")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",s.Handle);
        if(kind is "noAccess" or "badAccess"){request.Headers.Remove("X-Cerberus-Vault-Access");if(kind=="badAccess")request.Headers.Add("X-Cerberus-Vault-Access","invalid");}
        if(kind=="noToken")request.Headers.Authorization=null;if(kind=="badToken")request.Headers.Authorization=new("Bearer","invalid");
        if(kind=="scope")request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor,Guid.NewGuid()));
        if(kind is "empty" or "oversized" or "bom" or "utf8")
        {
            request.Content=new ByteArrayContent(kind switch{"empty"=>[],"oversized"=>Encoding.UTF8.GetBytes(new string(' ',65537)),"bom"=>[239,187,191,..Encoding.UTF8.GetBytes("{\"expectedRevision\":1}")],_=>[0xc3,0x28]});request.Content.Headers.ContentType=new("application/json");
        }
        if(kind=="compression")request.Content!.Headers.ContentEncoding.Add("gzip");
        if(kind is "noMedia" or "text" or "parameter" or "charset" or "duplicateCharset")
        {
            request.Content!.Headers.Remove("Content-Type");if(kind!="noMedia")request.Content.Headers.TryAddWithoutValidation("Content-Type",kind switch{"text"=>"text/plain","parameter"=>"application/json; boundary=forged","charset"=>"application/json; charset=utf-16",_=>"application/json; charset=utf-8; charset=utf-8"});
        }
        using var response=await Gateway.Client.SendAsync(request);Assert.Equal(status,(int)response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("missing")][InlineData("old")][InlineData("future")][InlineData("duplicate")][InlineData("malformed")]
    public async Task GivenUnfreshSignedIdentity_WhenDeleting_ThenNeverCommitErasure(string kind)
    {
        var s=await Setup();var before=await Snapshot(s);var claims=new List<System.Security.Claims.Claim>{new("id",s.Actor.ToString()),new("roleId","3"),new("scopeId",RegistrationApiFixture.Scope.ToString())};
        if(kind!="missing")claims.Add(new("iat",kind=="malformed"?"invalid":(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+(kind=="old"?-120:kind=="future"?120:0)).ToString(),kind=="malformed"?System.Security.Claims.ClaimValueTypes.String:System.Security.Claims.ClaimValueTypes.Integer64));
        if(kind=="duplicate")claims.Add(new("iat",DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),System.Security.Claims.ClaimValueTypes.Integer64));
        var token=new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken("fixture-issuer","fixture-audience",claims,DateTime.UtcNow.AddMinutes(-5),DateTime.UtcNow.AddMinutes(5),new Microsoft.IdentityModel.Tokens.SigningCredentials(new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.ASCII.GetBytes("fixture-only-signing-key-32-characters")),Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256)));
        using var request=Request(s);request.Headers.Authorization=new("Bearer",token);using var response=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);Assert.Equal(before,await Snapshot(s));
    }
    private async Task<string> Snapshot(State s)
    {
        await using var db=fixture.Context();return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object('account',(SELECT to_jsonb(a) FROM cerberus.account a WHERE public_id={s.AccountId}),
              'sessions',(SELECT jsonb_agg(to_jsonb(v) ORDER BY v.id) FROM cerberus.vault_access_session v JOIN cerberus.account a ON a.id=v.account_id WHERE a.public_id={s.AccountId}),
              'terminal',(SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM cerberus.terminal_erasure t WHERE resource_kind='account' AND resource_id={s.AccountId}),
              'work',(SELECT jsonb_agg(to_jsonb(w) ORDER BY id) FROM cerberus.retention_work_item w WHERE operation_key IN({"account-erase/"+s.AccountId},{"account-purge/"+s.AccountId})))::text AS "Value"
            """).SingleAsync();
    }
}
