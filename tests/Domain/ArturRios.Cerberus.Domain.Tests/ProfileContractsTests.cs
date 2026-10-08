using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;

namespace ArturRios.Cerberus.Domain.Tests;

public class ProfileContractsTests
{
    private readonly Guid _owner = Guid.NewGuid(), _id = Guid.NewGuid(), _actor = Guid.NewGuid();
    [UnitTheory][InlineData("Master")][InlineData("PerProfile")]
    public void GivenNativeOwnerWrapper_WhenCreatingProfile_ThenAcceptBothModes(string mode)
    {
        using var client=new ProtectionFixture(); using var scoped=new ProtectionFixture();
        var input=Input(client,scoped,mode);
        Assert.True(input.IsValid()); Assert.True(input.KeyWrappers.IsBound(_owner,_id,1,1,_actor,client.Material));
        Assert.True(input.KeyWrappers.MasterKeyWrapper.Verify(_owner,"profile",_id,1,_id,1,_actor,client.Material.RecipientKey,client.Material.AuthorKey));
    }
    [UnitTheory]
    [InlineData("format")][InlineData("epoch")][InlineData("unsafe")][InlineData("grant")][InlineData("revision")]
    [InlineData("identity")][InlineData("fingerprint")][InlineData("author")][InlineData("enc")][InlineData("point")]
    [InlineData("ciphertext")][InlineData("signature")][InlineData("padded")]
    public void GivenMalformedNativeWrapper_WhenValidating_ThenReject(string field)
    {
        using var client=new ProtectionFixture();var w=client.Wrap(_owner,"profile",_id,_actor);
        w=field switch
        {
            "format"=>w with {Format="other"},"epoch"=>w with {KeyEpoch=0},"unsafe"=>w with {KeyEpoch=9007199254740992},
            "grant"=>w with {GrantId=Guid.Empty},"revision"=>w with {GrantRevision=0},"identity"=>w with {RecipientIdentityId=Guid.Empty},
            "fingerprint"=>w with {RecipientKeyFingerprint="AQ"},"author"=>w with {AuthorKeyFingerprint="AQ"},
            "enc"=>w with {Enc="AQ"},"point"=>w with {Enc=ProtectionFixture.Encode(new byte[65])},
            "ciphertext"=>w with {Ciphertext="AQ"},"signature"=>w with {Signature="AQ"},"padded"=>w with {Ciphertext=w.Ciphertext+"="},_=>w
        };Assert.False(w.IsValid());
    }
    [UnitTheory]
    [InlineData("owner")][InlineData("kind")][InlineData("resource")][InlineData("epoch")][InlineData("grant")]
    [InlineData("revision")][InlineData("identity")][InlineData("recipient")][InlineData("author")][InlineData("ciphertext")][InlineData("signature")]
    public void GivenWrongTrustedBinding_WhenCheckingNativeAuthorProof_ThenReject(string field)
    {
        using var client=new ProtectionFixture();using var other=new ProtectionFixture();var w=client.Wrap(_owner,"profile",_id,_actor);
        if(field=="ciphertext")w=w with {Ciphertext=ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(48))};
        if(field=="signature")w=w with {Signature=ProtectionFixture.Encode(new byte[64])};
        Assert.False(w.Verify(field=="owner"?Guid.NewGuid():_owner,field=="kind"?"record":"profile",field=="resource"?Guid.NewGuid():_id,
            field=="epoch"?2:1,field=="grant"?Guid.NewGuid():_id,field=="revision"?2:1,field=="identity"?Guid.NewGuid():_actor,
            field=="recipient"?other.Material.RecipientKey:client.Material.RecipientKey,field=="author"?other.Material.AuthorKey:client.Material.AuthorKey));
    }
    [UnitTheory]
    [InlineData("id")][InlineData("envelope")][InlineData("epoch")][InlineData("wrappers")][InlineData("mode")]
    [InlineData("missingPassword")][InlineData("extraPassword")][InlineData("unlock")][InlineData("time")][InlineData("offset")]
    [InlineData("records")][InlineData("folders")][InlineData("collections")][InlineData("emptyId")][InlineData("duplicate")]
    public void GivenMalformedProfile_WhenValidating_ThenReject(string field)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var i=Input(client,scoped,"Master");
        i=field switch
        {
            "id"=>i with {ProfileId=Guid.Empty},"envelope"=>i with {Envelope=null!},"epoch"=>i with {Envelope=i.Envelope with {KeyEpoch=2}},
            "wrappers"=>i with {KeyWrappers=null!},"mode"=>i with {KeyWrappers=i.KeyWrappers with {UnlockMode="other"}},
            "missingPassword"=>i with {KeyWrappers=i.KeyWrappers with {UnlockMode="PerProfile"}},
            "extraPassword"=>i with {KeyWrappers=i.KeyWrappers with {PasswordWrapper=scoped.Material.PasswordWrapper}},
            "unlock"=>i with {KeyWrappers=i.KeyWrappers with {UnlockVerifier=null!}},"time"=>i with {EditedAt=default},
            "offset"=>i with {EditedAt=DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(1))},
            "records"=>i with {RecordIds=null!},"folders"=>i with {FolderIds=null!},"collections"=>i with {CollectionIds=null!},
            "emptyId"=>i with {RecordIds=[Guid.Empty]},"duplicate"=>i with {RecordIds=[_id,_id]},_=>i
        };Assert.False(i.IsValid());
    }
    [UnitTheory][InlineData("Master")][InlineData("PerProfile")]
    public void GivenProfileRotation_WhenValidating_ThenRequireFreshCompleteScopedMaterial(string mode)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var old=Input(client,scoped,mode).KeyWrappers;
        var next=old with {MasterKeyWrapper=client.Wrap(_owner,"profile",_id,_actor,2,2),PasswordWrapper=mode=="PerProfile"?scoped.Rewrap().PasswordWrapper:null};
        Assert.True(next.IsValidRotation(old));Assert.True(next.IsBound(_owner,_id,2,2,_actor,client.Material));
        Assert.False(old.IsValidRotation(old));Assert.False((next with {UnlockMode="other"}).IsValidRotation(old));
        Assert.False((next with {UnlockVerifier=client.Material.UnlockVerifier}).IsValidRotation(old));
        if(mode=="PerProfile")Assert.False((next with {PasswordWrapper=old.PasswordWrapper}).IsValidRotation(old));
        var account=new ContentReplacement("account",_owner,1,Envelope());
        var profile=new ContentReplacement("profile",_id,1,Envelope() with {KeyEpoch=2},next);
        var change=new ProtectionChange(_owner,1,1,"rotate-content",client.Rewrap(),[account,profile]);
        Assert.True(change.IsValid());Assert.False((change with {ContentReplacements=[account,profile,profile]}).IsValid());
        Assert.False((change with {ContentReplacements=[profile]}).IsValid());
        Assert.False((change with {ContentReplacements=[account,profile with {KeyWrappers=null}]}).IsValid());
    }
    [UnitTheory][InlineData("unknown")][InlineData("duplicate")][InlineData("numeric")]
    public void GivenStrictProfileSchema_WhenDeserializing_ThenRejectWireExtras(string invalid)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var raw=JsonSerializer.Serialize(Input(client,scoped,"Master"),ProtectionFixture.Json);
        raw=invalid switch {"unknown"=>raw.Insert(1,"\"name\":\"plaintext\","),"duplicate"=>raw.Insert(1,"\"profileId\":\""+_id+"\","),_=>raw.Replace("\"keyEpoch\":1","\"keyEpoch\":\"1\"")};
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<ProfileCreateInput>(raw,ProtectionFixture.Json));
    }
    [UnitFact]
    public void GivenExecutedJavaAndPythonNativeRecipientVectors_WhenVerifying_ThenAcceptBothOriginalSignatures()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"recipient-vectors.json")));
        var recipient=doc.RootElement.GetProperty("recipient").Deserialize<PublicJwk>(ProtectionFixture.Json)!;
        var author=doc.RootElement.GetProperty("author").Deserialize<PublicJwk>(ProtectionFixture.Json)!;
        foreach(var node in doc.RootElement.GetProperty("wrappers").EnumerateArray())
        {
            var w=node.Deserialize<RecipientEnvelope>(ProtectionFixture.Json)!;
            Assert.True(w.Verify(Guid.Parse("00000000-0000-0000-0000-000000000001"),"record",Guid.Parse("00000000-0000-0000-0000-000000000004"),
                1,Guid.Parse("00000000-0000-0000-0000-000000000007"),1,Guid.Parse("00000000-0000-0000-0000-000000000002"),recipient,author));
        }
    }
    private ProfileCreateInput Input(ProtectionFixture client,ProtectionFixture scoped,string mode)=>new(_id,Envelope(),
        new(mode,scoped.Material.UnlockVerifier,client.Wrap(_owner,"profile",_id,_actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null),DateTimeOffset.UtcNow,[],[],[]);
    public static EncryptedEnvelope Envelope()=>new("cerberus-content-v1",1,ProtectionFixture.Encode(new byte[32]),ProtectionFixture.Encode(new byte[12]),"AQID",ProtectionFixture.Encode(new byte[16]));
}
