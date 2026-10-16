using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IdentityModel.Tokens.Jwt;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public partial class AccountExportHttpTests(RegistrationApiFixture fixture):WebApiTest<Program>(EnvironmentType.Local)
{
    private const string ExportPath="/api/accounts/me/export";
    private const string AccessHeader="X-Cerberus-Vault-Access";
    private static readonly JsonSerializerOptions ExportJson=new(JsonSerializerDefaults.Web)
    {AllowDuplicateProperties=false,AllowOutOfOrderMetadataProperties=true,PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};

    [FunctionalFact]
    public async Task GivenCurrentOwnerWithAllPortableEntryKinds_WhenExporting_ThenStreamClosedArchiveWithoutMutationsOrForeignSecrets()
    {
        // Given: owner data, both sharing directions, software keys and retained history.
        var s=await SetupExport(rich:true);var before=await ExportSnapshot(s);var tag="uc54_complete_"+Guid.NewGuid().ToString("N");using var host=ExportWireHost(tag:tag);using var client=host.CreateClient();using var request=ExportRequest(s);request.Headers.TryAddWithoutValidation("If-None-Match","*");request.Headers.IfModifiedSince=DateTimeOffset.UtcNow;
        // When: consume the actual controller/mediator/PostgreSQL export stream.
        using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var raw=await response.Content.ReadAsStringAsync();Assert.Equal(0,await ExportBackendTransactions(tag));
        // Then: actual typed bytes/metadata form a complete attachment, never a foreign vault dump.
        Assert.True(response.Headers.CacheControl?.NoStore);Assert.False(response.Headers.Contains("ETag"));Assert.Null(response.Content.Headers.LastModified);
        Assert.Equal("application/json",response.Content.Headers.ContentType?.MediaType);Assert.Equal("attachment",response.Content.Headers.ContentDisposition?.DispositionType);Assert.Equal("cerberus-account-export.json",response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var doc=JsonDocument.Parse(raw);Assert.Equal(new[]{"complete","entries","entryCount","format","header"},doc.RootElement.EnumerateObject().Select(p=>p.Name).Order());
        var archive=JsonSerializer.Deserialize<AccountExportArchive>(raw,ExportJson)!;Assert.Equal("cerberus-account-export-v1",archive.Format);Assert.True(archive.Complete);Assert.Equal(archive.Entries.Count,archive.EntryCount);Assert.Equal(s.AccountId,archive.Header.AccountId);Assert.Equal(s.Actor,archive.Header.IdentityId);Assert.Equal(1,archive.Header.Revision);
        var kinds=doc.RootElement.GetProperty("entries").EnumerateArray().Select(e=>e.GetProperty("entryType").GetString()).Distinct().Order().ToArray();
        Assert.Equal(new[]{"account","collection","collection-grant","folder","profile","protection","record","relationship","shared-collection-reference","software-grant","software-target","trash-operation"},kinds);
        Assert.Equal(s.RecordEnvelope,archive.Entries.OfType<AccountExportRecord>().Single(r=>r.ResourceId==s.RecordId).Envelope);
        Assert.DoesNotContain(s.Handle,raw);Assert.DoesNotContain(s.Verifier,raw);Assert.DoesNotContain("handleVerifier",raw);Assert.DoesNotContain("associationSnapshot",raw);Assert.DoesNotContain("fences",raw);
        Assert.DoesNotContain(s.ForeignProfile.ToString(),raw);Assert.DoesNotContain(Convert.ToBase64String(s.ForeignCanary),raw);Assert.DoesNotContain(Convert.ToBase64String(s.RootKey),raw);Assert.DoesNotContain("private export transport fixture",raw);
        Assert.Equal(before,await ExportSnapshot(s));await SaveExportBody("success",raw);
    }

    [FunctionalFact]
    public async Task GivenDisabledRenewalPolicy_WhenExporting_ThenEmitExplicitNullIntervalInActualHeader()
    {
        var s=await SetupExport();await using(var db=fixture.Context())
        {await db.Accounts.Where(x=>x.Id==s.OwnerId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.RenewalEnabled,false).SetProperty(a=>a.RenewalInterval,(TimeSpan?)null));await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,(DateTimeOffset?)null));}
        var before=await ExportSnapshot(s);using var request=ExportRequest(s);using var response=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var raw=await response.Content.ReadAsStringAsync();using var doc=JsonDocument.Parse(raw);var header=doc.RootElement.GetProperty("header");Assert.False(header.GetProperty("renewalEnabled").GetBoolean());Assert.Equal(JsonValueKind.Null,header.GetProperty("renewalIntervalSeconds").ValueKind);Assert.Equal(before,await ExportSnapshot(s));await SaveExportBody("success-disabled-policy",raw);
    }

    [FunctionalFact]
    public async Task GivenGenuineContentAead_WhenExportedBytesAreLocallyDecoded_ThenOriginalContextDecryptsAndWrongContextFails()
    {
        // Given: test-only AES256GCM/HKDF content; the root never enters API/configuration.
        var s=await SetupExport();using var request=ExportRequest(s);using var response=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var archive=JsonSerializer.Deserialize<AccountExportArchive>(await response.Content.ReadAsStringAsync(),ExportJson)!;var record=archive.Entries.OfType<AccountExportRecord>().Single(x=>x.ResourceId==s.RecordId);var envelope=JsonSerializer.Deserialize<EncryptedEnvelope>(record.Envelope,ExportJson)!;
        // When/Then: this proves narrow content transport, not structural fixture HPKE/password decryption.
        Assert.Equal(s.RecordEnvelope,record.Envelope);Assert.Equal("private export transport fixture",Encoding.UTF8.GetString(DecryptContent(s.RootKey,archive.Header.AccountId,record.ResourceId,envelope)));
        Assert.ThrowsAny<CryptographicException>(()=>DecryptContent(s.RootKey,Guid.NewGuid(),record.ResourceId,envelope));Assert.ThrowsAny<CryptographicException>(()=>DecryptContent(s.RootKey,archive.Header.AccountId,Guid.NewGuid(),envelope));
    }

    [FunctionalTheory][InlineData("{}")][InlineData("null")][InlineData("[]")][InlineData("{")]
    [InlineData("{\"expectedRevision\":1,\"expectedRevision\":1}")][InlineData("{\"expectedRevision\":1,\"ownerId\":\"forged\"}")]
    [InlineData("{\"ExpectedRevision\":1}")][InlineData("{\"expectedRevision\":\"1\"}")][InlineData("{\"expectedRevision\":1.0}")][InlineData("{\"expectedRevision\":1e0}")]
    [InlineData("{\"expectedRevision\":null}")][InlineData("{\"expectedRevision\":0}")][InlineData("{\"expectedRevision\":-1}")][InlineData("{\"expectedRevision\":9007199254740992}")]
    public async Task GivenInvalidExportInput_WhenRequesting_ThenRejectWithoutNativeOrSessionChanges(string body)
    {
        var s=await SetupExport();var before=await ExportSnapshot(s);using var request=ExportRequest(s,body);using var response=await Gateway.Client.SendAsync(request);await ExportFailure(response,400,"validation_failed");Assert.Equal(before,await ExportSnapshot(s));
    }

    [FunctionalTheory][InlineData("query",400)][InlineData("duplicate",400)][InlineData("emptyBody",400)][InlineData("oversized",413)]
    [InlineData("noMedia",400)][InlineData("text",400)][InlineData("parameter",400)][InlineData("charset",400)][InlineData("duplicateCharset",400)]
    [InlineData("compression",400)][InlineData("bom",400)][InlineData("utf8",400)][InlineData("emptyHandle",400)][InlineData("badHandle",400)]
    [InlineData("noAccess",401)][InlineData("noToken",401)][InlineData("badToken",401)][InlineData("scope",401)]
    [InlineData("deletedIdentity",401)][InlineData("identityUnavailable",503)][InlineData("missing",404)][InlineData("closing",404)][InlineData("terminal",404)]
    [InlineData("revoked",403)][InlineData("expired",403)][InlineData("selected",403)][InlineData("revision",409)]
    public async Task GivenInvalidTransportOrAuthority_WhenExporting_ThenReturnSafeActualProcessOutputAndPreserveGraph(string kind,int status)
    {
        var s=await SetupExport(state:kind);var before=await ExportSnapshot(s);using var request=ExportRequest(s,kind=="revision"?"{\"expectedRevision\":2}":"{\"expectedRevision\":1}");
        if(kind=="query")request.RequestUri=new(ExportPath+"?ownerId=forged",UriKind.Relative);if(kind=="duplicate")request.Headers.TryAddWithoutValidation(AccessHeader,s.Handle);
        if(kind is "noAccess" or "badHandle" or "emptyHandle"){request.Headers.Remove(AccessHeader);if(kind!="noAccess")request.Headers.TryAddWithoutValidation(AccessHeader,kind=="badHandle"?"invalid":" ");}
        if(kind=="noToken")request.Headers.Authorization=null;if(kind=="badToken")request.Headers.Authorization=new("Bearer","invalid");if(kind=="scope")request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor,Guid.NewGuid()));
        if(kind is "emptyBody" or "oversized" or "bom" or "utf8")
        {request.Content=new ByteArrayContent(kind switch{"emptyBody"=>[],"oversized"=>Encoding.UTF8.GetBytes(new string(' ',65537)),"bom"=>[239,187,191,..Encoding.UTF8.GetBytes("{\"expectedRevision\":1}")],_=>[0xc3,0x28]});request.Content.Headers.ContentType=new("application/json");}
        if(kind=="compression")request.Content!.Headers.ContentEncoding.Add("gzip");
        if(kind is "noMedia" or "text" or "parameter" or "charset" or "duplicateCharset")
        {request.Content!.Headers.Remove("Content-Type");if(kind!="noMedia")request.Content.Headers.TryAddWithoutValidation("Content-Type",kind switch{"text"=>"text/plain","parameter"=>"application/json; boundary=forged","charset"=>"application/json; charset=utf-16",_=>"application/json; charset=utf-8; charset=utf-8"});}
        using var response=await Gateway.Client.SendAsync(request);await ExportFailure(response,status);Assert.Equal(before,await ExportSnapshot(s));if(status!=413)await SaveExportBody("error-"+status,await response.Content.ReadAsStringAsync());
    }

    [FunctionalTheory][InlineData("missing",401)][InlineData("duplicate",401)][InlineData("malformed",401)][InlineData("old",403)][InlineData("future",403)]
    public async Task GivenInvalidSignedFreshness_WhenExporting_ThenUseStableAuthorityStatusWithoutExportingBytes(string kind,int status)
    {
        var s=await SetupExport();var before=await ExportSnapshot(s);using var request=ExportRequest(s);var claims=new List<Claim>{new("id",s.Actor.ToString()),new("roleId","3"),new("scopeId",RegistrationApiFixture.Scope.ToString())};
        if(kind!="missing")claims.Add(new("iat",kind=="malformed"?"invalid":(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+(kind=="old"?-120:kind=="future"?120:0)).ToString(System.Globalization.CultureInfo.InvariantCulture),kind=="malformed"?ClaimValueTypes.String:ClaimValueTypes.Integer64));
        if(kind=="duplicate")claims.Add(new("iat",DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),ClaimValueTypes.Integer64));request.Headers.Authorization=new("Bearer",SignedExportToken(claims));
        using var response=await Gateway.Client.SendAsync(request);await ExportFailure(response,status);Assert.Equal(before,await ExportSnapshot(s));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenSoftwareOrOperationalCredential_WhenExportingOwnerVault_ThenRefuseImpersonation(bool software)
    {
        var s=await SetupExport();var before=await ExportSnapshot(s);IEnumerable<Claim> claims=software
            ?[new("id",Guid.NewGuid().ToString()),new("scopeId",RegistrationApiFixture.Scope.ToString()),new("credentialId",Guid.NewGuid().ToString()),new("tokenKind","application")]
            :[new("id",Guid.NewGuid().ToString()),new("roleId","2"),new("ownedScopeIds",RegistrationApiFixture.Scope.ToString())];
        using var request=ExportRequest(s);request.Headers.Authorization=new("Bearer",SignedExportToken(claims,software?"fixture-applications":"fixture-audience"));using var response=await Gateway.Client.SendAsync(request);await ExportFailure(response,401);Assert.Equal(before,await ExportSnapshot(s));
    }

    private sealed record ExportState(Guid Actor,Guid AccountId,long OwnerId,string Handle,string Verifier,Guid RecordId,byte[] RecordEnvelope,byte[] RootKey,long ForeignOwnerId,Guid ForeignProfile,byte[] ForeignCanary);
    private async Task<ExportState> SetupExport(bool rich=false,string state="active",int extraRecords=0)
    {
        var actor=Guid.NewGuid();var accountId=Guid.NewGuid();var recordId=Guid.NewGuid();byte[] root=RandomNumberGenerator.GetBytes(32);var recordBytes=EncryptContent(root,accountId,recordId);var handle=ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32));Assert.True(OpaqueAccessHandle.TryHash(handle,out var verifier));
        fixture.Users[Guid.NewGuid().ToString("N")+"@example.test"]=new(actor,"fixture-password",Deleted:state=="deletedIdentity",IdentityUnavailable:state=="identityUnavailable");
        await using var db=fixture.Context();var owner=new Account{PublicId=accountId,HeimdallPublicId=actor,DetailsEnvelope=[11,13,17],State=state=="closing"?AccountState.ClosurePending:AccountState.Active};db.Accounts.Add(owner);await db.SaveChangesAsync();
        using var protection=new ProtectionFixture();db.VaultProtections.Add(new(){AccountId=owner.Id,Material=JsonSerializer.SerializeToUtf8Bytes(protection.Material,ExportJson),Revision=1,KeyEpoch=1,RecoveryGeneration=1});
        db.VaultAccessSessions.Add(new(){AccountId=owner.Id,HandleVerifier=verifier,ProfileId=state=="selected"?777:null,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-5),ExpiresAt=state=="expired"?DateTimeOffset.UtcNow.AddHours(-1):DateTimeOffset.UtcNow.AddHours(1),Revoked=state=="revoked",PolicyRevision=1,RevocationGeneration=1});
        var edited=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());var record=new VaultRecord{AccountId=owner.Id,PublicId=recordId,Envelope=recordBytes,EditedAt=edited};db.Records.Add(record);
        for(var i=0;i<extraRecords;i++)db.Records.Add(new(){AccountId=owner.Id,PublicId=Guid.NewGuid(),Envelope=[21,23,29],EditedAt=edited});
        if(state=="terminal")db.TerminalErasures.Add(new(){ResourceKind="account",ResourceId=accountId,DeletedAt=edited});await db.SaveChangesAsync();
        long foreignId=0;var foreignProfile=Guid.Empty;byte[] canary=Encoding.UTF8.GetBytes("private-foreign-export-canary-"+Guid.NewGuid().ToString("N"));
        if(rich)
        {
            var foreign=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=Guid.NewGuid(),DetailsEnvelope=canary};db.Accounts.Add(foreign);await db.SaveChangesAsync();foreignId=foreign.Id;foreignProfile=Guid.NewGuid();
            db.Profiles.Add(new(){AccountId=foreign.Id,PublicId=foreignProfile,Envelope=canary,KeyWrappers=canary,EditedAt=edited});
            var profile=new ArturRios.Cerberus.Domain.Profiles.Profile{AccountId=owner.Id,PublicId=Guid.NewGuid(),Envelope=[31,37],KeyWrappers=[41,43],EditedAt=edited};var folder=new VaultFolder{AccountId=owner.Id,PublicId=Guid.NewGuid(),Envelope=[47,53],EditedAt=edited};var collection=new VaultCollection{AccountId=owner.Id,PublicId=Guid.NewGuid(),Envelope=[59,61],EditedAt=edited};var shared=new VaultCollection{AccountId=foreign.Id,PublicId=Guid.NewGuid(),Envelope=canary,EditedAt=edited};
            db.Profiles.Add(profile);db.Folders.Add(folder);db.Collections.AddRange(collection,shared);await db.SaveChangesAsync();record.FolderId=folder.Id;
            db.ProfileRecords.Add(new(){AccountId=owner.Id,ProfileId=profile.Id,RecordId=record.Id});db.ProfileFolders.Add(new(){AccountId=owner.Id,ProfileId=profile.Id,FolderId=folder.Id});db.ProfileCollections.AddRange(new(){ProfileId=profile.Id,CollectionId=collection.Id},new(){ProfileId=profile.Id,CollectionId=shared.Id});db.CollectionRecords.Add(new(){AccountId=owner.Id,CollectionId=collection.Id,RecordId=record.Id});db.CollectionFolders.Add(new(){AccountId=owner.Id,CollectionId=collection.Id,FolderId=folder.Id});
            db.CollectionGrants.AddRange(new(){PublicId=Guid.NewGuid(),CollectionId=collection.Id,RecipientAccountId=foreign.Id,RecipientKeyEnvelope=[67,71]},new(){PublicId=Guid.NewGuid(),CollectionId=shared.Id,RecipientAccountId=owner.Id,RecipientKeyEnvelope=canary});
            var software=new SoftwareGrant{PublicId=Guid.NewGuid(),AccountId=owner.Id,HeimdallSoftwarePublicId=Guid.NewGuid(),RecipientKey=[73,79]};db.SoftwareGrants.Add(software);await db.SaveChangesAsync();db.SoftwareGrantRecords.Add(new(){AccountId=owner.Id,GrantId=software.Id,RecordId=record.Id,RecipientKeyEnvelope=[83,89]});db.SoftwareGrantCollections.Add(new(){AccountId=owner.Id,GrantId=software.Id,CollectionId=collection.Id,RecipientKeyEnvelope=[97,101]});
            var deleted=edited.AddHours(-1);var retained=new VaultRecord{AccountId=owner.Id,PublicId=Guid.NewGuid(),Envelope=[103,107],EditedAt=edited,DeletedAt=deleted,PurgeAt=deleted.AddHours(720)};db.Records.Add(retained);var op=new TrashOperation{AccountId=owner.Id,RootResourceKind="record",RootResourceId=retained.PublicId,DeletedAt=deleted,PurgeAt=deleted.AddHours(720)};db.TrashOperations.Add(op);await db.SaveChangesAsync();db.TrashEntries.Add(new(){OperationId=op.Id,ResourceKind="record",ResourceId=retained.PublicId,AssociationSnapshot=JsonSerializer.SerializeToUtf8Bytes(new RecordAssociationSnapshot(folder.PublicId,[profile.PublicId],[collection.PublicId]),ExportJson)});await db.SaveChangesAsync();
        }
        if(state=="missing")await db.Accounts.Where(x=>x.Id==owner.Id).ExecuteDeleteAsync();return new(actor,accountId,owner.Id,handle,verifier,recordId,recordBytes,root,foreignId,foreignProfile,canary);
    }
    private static HttpRequestMessage ExportRequest(ExportState s,string body="{\"expectedRevision\":1}")
    {var request=new HttpRequestMessage(HttpMethod.Post,ExportPath){Content=new StringContent(body,Encoding.UTF8,"application/json")};request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));request.Headers.Add(AccessHeader,s.Handle);return request;}
    private async Task<string> ExportSnapshot(ExportState s)
    {
        await using var db=fixture.Context();var ids=new[]{s.OwnerId,s.ForeignOwnerId};return JsonSerializer.Serialize(new{
            Accounts=await db.Accounts.AsNoTracking().Where(x=>ids.Contains(x.Id)).OrderBy(x=>x.Id).ToArrayAsync(),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>ids.Contains(x.AccountId)).OrderBy(x=>x.Id).ToArrayAsync(),Protection=await db.VaultProtections.AsNoTracking().Where(x=>ids.Contains(x.AccountId)).OrderBy(x=>x.Id).ToArrayAsync(),
            Records=await db.Records.AsNoTracking().Where(x=>ids.Contains(x.AccountId)).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>ids.Contains(x.AccountId)).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>ids.Contains(x.AccountId)).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>ids.Contains(x.AccountId)).OrderBy(x=>x.Id).ToArrayAsync(),
            Software=await db.SoftwareGrants.AsNoTracking().Where(x=>ids.Contains(x.AccountId)).OrderBy(x=>x.Id).ToArrayAsync(),Targets=await db.SoftwareGrantRecords.AsNoTracking().Where(x=>ids.Contains(x.AccountId)).OrderBy(x=>x.GrantId).ThenBy(x=>x.RecordId).ToArrayAsync(),Operations=await db.TrashOperations.AsNoTracking().Where(x=>ids.Contains(x.AccountId)).OrderBy(x=>x.Id).ToArrayAsync(),Markers=await db.TerminalErasures.AsNoTracking().OrderBy(x=>x.ResourceKind).ThenBy(x=>x.ResourceId).ToArrayAsync()},ExportJson);
    }
    private static async Task ExportFailure(HttpResponseMessage response,int status,string? error=null)
    {Assert.Equal(status,(int)response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);Assert.False(response.Headers.Contains("ETag"));if(status==413)return;var raw=await response.Content.ReadAsStringAsync();using var doc=JsonDocument.Parse(raw);Assert.False(doc.RootElement.GetProperty("success").GetBoolean());Assert.False(doc.RootElement.TryGetProperty("data",out _));Assert.False(doc.RootElement.TryGetProperty("entries",out _));if(error is not null)Assert.Contains(error,doc.RootElement.GetProperty("errors").EnumerateArray().Select(e=>e.GetString()));Assert.DoesNotContain("fixture-password",raw);}
    private static string SignedExportToken(IEnumerable<Claim> claims,string audience="fixture-audience")=>new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("fixture-issuer",audience,claims,DateTime.UtcNow.AddMinutes(-5),DateTime.UtcNow.AddMinutes(5),new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes("fixture-only-signing-key-32-characters")),SecurityAlgorithms.HmacSha256)));
    private static byte[] EncryptContent(byte[] root,Guid owner,Guid resource)
    {
        byte[] salt=RandomNumberGenerator.GetBytes(32),nonce=RandomNumberGenerator.GetBytes(12),plain=Encoding.UTF8.GetBytes("private export transport fixture"),cipher=new byte[plain.Length],tag=new byte[16];var encodedSalt=ProtocolBinary.Encode(salt);var key=ContentKey(root,owner,resource,1,salt);using(var aead=new AesGcm(key,16))aead.Encrypt(nonce,plain,cipher,tag,ContentAad(owner,resource,1,encodedSalt));CryptographicOperations.ZeroMemory(key);return JsonSerializer.SerializeToUtf8Bytes(new EncryptedEnvelope("cerberus-content-v1",1,encodedSalt,ProtocolBinary.Encode(nonce),ProtocolBinary.Encode(cipher),ProtocolBinary.Encode(tag)),ExportJson);
    }
    private static byte[] DecryptContent(byte[] root,Guid owner,Guid resource,EncryptedEnvelope e)
    {Assert.True(ProtocolBinary.TryDecode(e.KeySalt,32,out var salt));Assert.True(ProtocolBinary.TryDecode(e.Nonce,12,out var nonce));Assert.True(ProtocolBinary.TryDecode(e.Ciphertext,null,out var cipher));Assert.True(ProtocolBinary.TryDecode(e.Tag,16,out var tag));var key=ContentKey(root,owner,resource,e.KeyEpoch,salt!);try{var plain=new byte[cipher!.Length];using var aead=new AesGcm(key,16);aead.Decrypt(nonce!,cipher,tag!,plain,ContentAad(owner,resource,e.KeyEpoch,e.KeySalt));return plain;}finally{CryptographicOperations.ZeroMemory(key);}}
    private static byte[] ContentKey(byte[] root,Guid owner,Guid resource,long epoch,byte[] salt)=>HKDF.DeriveKey(HashAlgorithmName.SHA256,root,32,salt,JsonSerializer.SerializeToUtf8Bytes(new object[]{"cerberus-aead-key-v1","cerberus-content-v1",owner.ToString("D"),"record",resource.ToString("D"),epoch,Array.Empty<object>()}));
    private static byte[] ContentAad(Guid owner,Guid resource,long epoch,string salt)=>JsonSerializer.SerializeToUtf8Bytes(new object[]{"cerberus-aead-v1","cerberus-content-v1",owner.ToString("D"),"record",resource.ToString("D"),epoch,Array.Empty<object>(),salt});
    private static Task SaveExportBody(string name,string raw)
    {var folder=Path.Combine(AppContext.BaseDirectory,"account-export-contract-fixtures");Directory.CreateDirectory(folder);return File.WriteAllTextAsync(Path.Combine(folder,name+".json"),raw);}
}
