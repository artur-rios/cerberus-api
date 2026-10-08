using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.TestSupport;
namespace ArturRios.Cerberus.Domain.Tests;
public class ProfileAccessContractTests
{
    [UnitTheory][InlineData(1)][InlineData(9007199254740991)]
    public void GivenValidProfileProofRequests_WhenValidating_ThenAcceptSafeRevisionAndNativeDigest(long revision)
    {
        var actor=Guid.NewGuid();var id=Guid.NewGuid();var digest=ProtectionFixture.Encode(new byte[32]);
        Assert.True(new ProfileChallengeRequest(actor,id,revision,digest).IsValid());
        Assert.True(new ProfileAccessRequest(actor,id,revision,Guid.NewGuid(),ProtectionFixture.Encode(new byte[64]),"{}"u8.ToArray()).IsValid());
        Assert.True(new ProfileAccessInput(revision).IsValid());
    }
    [UnitTheory][InlineData("actor")][InlineData("id")][InlineData("zero")][InlineData("negative")][InlineData("unsafe")][InlineData("digest")][InlineData("padded")]
    public void GivenInvalidChallengeContext_WhenValidating_ThenReject(string kind)
    {
        var r=new ProfileChallengeRequest(Guid.NewGuid(),Guid.NewGuid(),1,ProtectionFixture.Encode(new byte[32]));
        r=kind switch {"actor"=>r with{Actor=Guid.Empty},"id"=>r with{ProfileId=Guid.Empty},"zero"=>r with{ExpectedRevision=0},"negative"=>r with{ExpectedRevision=-1},"unsafe"=>r with{ExpectedRevision=9007199254740992},"digest"=>r with{RequestHash="invalid"},_=>r with{RequestHash=r.RequestHash+"="}};
        Assert.False(r.IsValid());
    }
    [UnitTheory][InlineData("actor")][InlineData("id")][InlineData("revision")][InlineData("challenge")][InlineData("proof")][InlineData("body")][InlineData("nullBody")][InlineData("largeBody")]
    public void GivenInvalidAccessRequest_WhenValidating_ThenReject(string kind)
    {
        var r=new ProfileAccessRequest(Guid.NewGuid(),Guid.NewGuid(),1,Guid.NewGuid(),ProtectionFixture.Encode(new byte[64]),"{}"u8.ToArray());
        r=kind switch {"actor"=>r with{Actor=Guid.Empty},"id"=>r with{ProfileId=Guid.Empty},"revision"=>r with{ExpectedRevision=0},"challenge"=>r with{ChallengeId=Guid.Empty},"proof"=>r with{Proof="invalid"},"body"=>r with{RawBody=[]},"nullBody"=>r with{RawBody=null!},_=>r with{RawBody=new byte[1048577]}};
        Assert.False(r.IsValid());
    }
    [UnitTheory][InlineData("missing")][InlineData("unknown")][InlineData("duplicate")][InlineData("numeric")][InlineData("fraction")]
    public void GivenAmbiguousOrIncompleteSignedBody_WhenParsing_ThenReject(string kind)
    {
        var raw=kind switch {"missing"=>"{}","unknown"=>"{\"expectedRevision\":1,\"actor\":null}","duplicate"=>"{\"expectedRevision\":1,\"expectedRevision\":1}","numeric"=>"{\"expectedRevision\":\"1\"}",_=>"{\"expectedRevision\":1.0}"};
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<ProfileAccessInput>(raw,ProtectionFixture.Json));
    }
    [UnitTheory][InlineData("Master")][InlineData("PerProfile")]
    public void GivenNativeScopedMaterialAndTypedContext_WhenValidating_ThenAcceptCurrentActorAndSelection(string mode)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var actor=Guid.NewGuid();var account=Guid.NewGuid();var id=Guid.NewGuid();var m=Material(owner,scoped,actor,account,id,mode);
        Assert.True(m.IsValid(actor,id,1));var shared=Guid.NewGuid();var context=new ProfileAccessContext(id,1,1,DateTimeOffset.UnixEpoch.AddTicks(10),m.Envelope,m.KeyWrappers,[shared],[shared],[shared]);Assert.True(context.IsValid(account,actor,id,1));
        Assert.False(m.IsValid(Guid.NewGuid(),id,1));Assert.False(m.IsValid(actor,Guid.NewGuid(),1));Assert.False(m.IsValid(actor,id,2));
    }
    [UnitTheory][InlineData("account")][InlineData("revision")][InlineData("envelope")][InlineData("wrappers")][InlineData("grant")][InlineData("grantRevision")][InlineData("epoch")][InlineData("identity")]
    public void GivenCorruptNativeProfileMaterial_WhenValidating_ThenRejectBeforeReturningIt(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var actor=Guid.NewGuid();var account=Guid.NewGuid();var id=Guid.NewGuid();var m=Material(owner,scoped,actor,account,id,"Master");
        m=kind switch {"account"=>m with{AccountId=Guid.Empty},"revision"=>m with{Revision=0},"envelope"=>m with{Envelope=null!},"wrappers"=>m with{KeyWrappers=null!},"grant"=>m with{KeyWrappers=m.KeyWrappers with{MasterKeyWrapper=m.KeyWrappers.MasterKeyWrapper with{GrantId=Guid.NewGuid()}}},"grantRevision"=>m with{KeyWrappers=m.KeyWrappers with{MasterKeyWrapper=m.KeyWrappers.MasterKeyWrapper with{GrantRevision=2}}},"epoch"=>m with{Envelope=m.Envelope with{KeyEpoch=2}},_=>m with{KeyWrappers=m.KeyWrappers with{MasterKeyWrapper=m.KeyWrappers.MasterKeyWrapper with{RecipientIdentityId=Guid.NewGuid()}}}};
        Assert.False(m.IsValid(actor,id,1));
    }
    [UnitTheory][InlineData("sequence")][InlineData("unsafeSequence")][InlineData("time")][InlineData("submicrosecond")][InlineData("nullRecords")][InlineData("zeroFolder")][InlineData("duplicateCollections")]
    public void GivenInvalidSelectedContext_WhenValidating_ThenReject(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var actor=Guid.NewGuid();var account=Guid.NewGuid();var id=Guid.NewGuid();var m=Material(owner,scoped,actor,account,id,"Master");var c=new ProfileAccessContext(id,1,1,DateTimeOffset.UnixEpoch,m.Envelope,m.KeyWrappers,[],[],[]);var dup=Guid.NewGuid();
        c=kind switch {"sequence"=>c with{ServerSequence=0},"unsafeSequence"=>c with{ServerSequence=9007199254740992},"time"=>c with{EditedAt=default},"submicrosecond"=>c with{EditedAt=c.EditedAt.AddTicks(1)},"nullRecords"=>c with{RecordIds=null!},"zeroFolder"=>c with{FolderIds=[Guid.Empty]},_=>c with{CollectionIds=[dup,dup]}};
        Assert.False(c.IsValid(account,actor,id,1));
    }
    private static ProfileAccessMaterial Material(ProtectionFixture owner,ProtectionFixture scoped,Guid actor,Guid account,Guid id,string mode)=>new(account,id,1,new("cerberus-content-v1",1,ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32)),ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(12)),"AQID",ProtectionFixture.Encode(new byte[16])),new(mode,scoped.Material.UnlockVerifier,owner.Wrap(account,"profile",id,actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null));
}
