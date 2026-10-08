using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.TestSupport;
namespace ArturRios.Cerberus.Domain.Tests;

public class RecordCreateContractTests
{
    private static readonly JsonSerializerOptions Json=new(ProtectionFixture.Json){NumberHandling=JsonNumberHandling.Strict};
    [UnitTheory][InlineData("unlinked")][InlineData("folder")][InlineData("multipleProfiles")][InlineData("sameGuidDifferentKind")][InlineData("minimum")][InlineData("submicrosecond")][InlineData("pre2000")]
    public void GivenNativeRecordWithPermittedRelationshipShape_WhenValidating_ThenAccept(string kind)
    {
        var input=Input();input=kind switch
        {
            "folder"=>input with{FolderId=Guid.NewGuid()},
            "multipleProfiles"=>input with{ProfileIds=[Guid.NewGuid(),Guid.NewGuid()]},
            "sameGuidDifferentKind"=>input with{ProfileIds=[input.RecordId],FolderId=input.RecordId},
            "minimum"=>input with{EditedAt=new DateTimeOffset(10,TimeSpan.Zero)},
            "submicrosecond"=>input with{EditedAt=new DateTimeOffset(19,TimeSpan.Zero)},
            "pre2000"=>input with{EditedAt=new DateTimeOffset(1999,12,31,23,59,59,TimeSpan.Zero).AddTicks(19)},_=>input
        };
        Assert.True(input.IsValid());
    }
    [UnitTheory][InlineData("recordId")][InlineData("nullEnvelope")][InlineData("format")][InlineData("epoch")][InlineData("unsafeEpoch")][InlineData("salt")][InlineData("paddedSalt")][InlineData("nonce")][InlineData("tag")][InlineData("emptyCiphertext")][InlineData("paddedCiphertext")][InlineData("nullProfiles")][InlineData("zeroProfile")][InlineData("duplicateProfile")][InlineData("zeroFolder")][InlineData("defaultTime")][InlineData("roundsToDefault")][InlineData("nonUtcTime")]
    public void GivenInvalidVisibleRecordInput_WhenValidating_ThenReject(string kind)
    {
        var input=Input();var id=Guid.NewGuid();input=kind switch
        {
            "recordId"=>input with{RecordId=Guid.Empty},"nullEnvelope"=>input with{Envelope=null!},
            "format"=>input with{Envelope=input.Envelope with{Format="unsupported"}},
            "epoch"=>input with{Envelope=input.Envelope with{KeyEpoch=2}},
            "unsafeEpoch"=>input with{Envelope=input.Envelope with{KeyEpoch=9007199254740992}},
            "salt"=>input with{Envelope=input.Envelope with{KeySalt="AQID"}},
            "paddedSalt"=>input with{Envelope=input.Envelope with{KeySalt=input.Envelope.KeySalt+"="}},
            "nonce"=>input with{Envelope=input.Envelope with{Nonce="AQID"}},
            "tag"=>input with{Envelope=input.Envelope with{Tag="AQID"}},
            "emptyCiphertext"=>input with{Envelope=input.Envelope with{Ciphertext=""}},
            "paddedCiphertext"=>input with{Envelope=input.Envelope with{Ciphertext="AQID="}},
            "nullProfiles"=>input with{ProfileIds=null!},"zeroProfile"=>input with{ProfileIds=[Guid.Empty]},
            "duplicateProfile"=>input with{ProfileIds=[id,id]},"zeroFolder"=>input with{FolderId=Guid.Empty},
            "defaultTime"=>input with{EditedAt=default},"roundsToDefault"=>input with{EditedAt=new(9,TimeSpan.Zero)},
            _=>input with{EditedAt=DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(1))}
        };
        Assert.False(input.IsValid());
    }
    [UnitTheory][InlineData("recordId")][InlineData("envelope")][InlineData("editedAt")][InlineData("profileIds")]
    public void GivenMissingRequiredCreationMember_WhenParsing_ThenReject(string field)
    {
        var body=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(Input(),Json),Json)!;body.Remove(field);
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<RecordCreateInput>(JsonSerializer.Serialize(body),Json));
    }
    [UnitTheory][InlineData("accountId")][InlineData("actor")][InlineData("vaultAccess")][InlineData("revision")][InlineData("serverSequence")][InlineData("deletedAt")][InlineData("name")][InlineData("fields")][InlineData("templateDescriptor")][InlineData("collectionIds")]
    public void GivenForgedOwnershipMetadataOrPlaintextMembers_WhenParsing_ThenReject(string field)
    {
        var body=JsonSerializer.Serialize(Input(),Json).Insert(1,"\""+field+"\":null,");
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<RecordCreateInput>(body,Json));
    }
    [UnitTheory][InlineData("duplicate")][InlineData("wrongCase")][InlineData("nestedNumeric")][InlineData("fractionalEpoch")][InlineData("unknownEnvelope")]
    public void GivenAmbiguousOrNonNativeWireBody_WhenParsing_ThenReject(string kind)
    {
        var input=Input();var body=JsonSerializer.Serialize(input,Json);body=kind switch
        {
            "duplicate"=>body.Insert(1,"\"recordId\":\""+input.RecordId+"\","),
            "wrongCase"=>body.Replace("recordId","RecordId"),
            "nestedNumeric"=>body.Replace("\"keyEpoch\":1","\"keyEpoch\":\"1\""),
            "fractionalEpoch"=>body.Replace("\"keyEpoch\":1","\"keyEpoch\":1.5"),
            _=>body.Replace("\"envelope\":{","\"envelope\":{\"unknown\":1,")
        };
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<RecordCreateInput>(body,Json));
    }
    [UnitTheory][InlineData(false)][InlineData(true)]
    public void GivenAbsentOrExplicitNullFolder_WhenParsing_ThenAcceptUnfolderedRecord(bool absent)
    {
        var input=Input();var body=JsonSerializer.Serialize(input,Json);if(absent)body=body.Replace(",\"folderId\":null","");
        var parsed=JsonSerializer.Deserialize<RecordCreateInput>(body,Json)!;Assert.True(parsed.IsValid());Assert.Null(parsed.FolderId);Assert.Equal(input.RecordId,parsed.RecordId);Assert.Equal(input.Envelope,parsed.Envelope);
    }
    private static RecordCreateInput Input()=>new(Guid.NewGuid(),new EncryptedEnvelope("cerberus-content-v1",1,ProtectionFixture.Encode(new byte[32]),ProtectionFixture.Encode(new byte[12]),"AQID",ProtectionFixture.Encode(new byte[16])),DateTimeOffset.UnixEpoch,[]);
}
