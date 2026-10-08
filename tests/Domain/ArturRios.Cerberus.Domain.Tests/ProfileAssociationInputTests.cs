using System.Text.Json;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.TestSupport;
namespace ArturRios.Cerberus.Domain.Tests;
public class ProfileAssociationInputTests
{
    [UnitTheory][InlineData(1)][InlineData(9007199254740991)]
    public void GivenValidTypedSets_WhenValidating_ThenPermitEmptyAndSameGuidAcrossKinds(long revision)
    {var id=Guid.NewGuid();Assert.True(new ProfileAssociationInput(revision,[],[],[]).IsValid());Assert.True(new ProfileAssociationInput(revision,[id],[id],[id]).IsValid());}
    [UnitTheory][InlineData("recordNull")][InlineData("folderNull")][InlineData("collectionNull")][InlineData("recordEmpty")][InlineData("folderEmpty")][InlineData("collectionEmpty")][InlineData("recordDuplicate")][InlineData("folderDuplicate")][InlineData("collectionDuplicate")][InlineData("zeroRevision")][InlineData("negativeRevision")][InlineData("unsafeRevision")]
    public void GivenInvalidSetOrRevision_WhenValidating_ThenReject(string kind)
    {var id=Guid.NewGuid();var i=new ProfileAssociationInput(1,[],[],[]);i=kind switch {"recordNull"=>i with {RecordIds=null!},"folderNull"=>i with {FolderIds=null!},"collectionNull"=>i with {CollectionIds=null!},"recordEmpty"=>i with {RecordIds=[Guid.Empty]},"folderEmpty"=>i with {FolderIds=[Guid.Empty]},"collectionEmpty"=>i with {CollectionIds=[Guid.Empty]},"recordDuplicate"=>i with {RecordIds=[id,id]},"folderDuplicate"=>i with {FolderIds=[id,id]},"collectionDuplicate"=>i with {CollectionIds=[id,id]},"zeroRevision"=>i with {ExpectedRevision=0},"negativeRevision"=>i with {ExpectedRevision=-1},_=>i with {ExpectedRevision=9007199254740992}};Assert.False(i.IsValid());}
    [UnitTheory][InlineData("unknown")][InlineData("duplicate")][InlineData("numeric")]
    public void GivenStrictAssociationInput_WhenDeserializing_ThenRejectExtraOrAmbiguousWireFields(string kind)
    {var raw=JsonSerializer.Serialize(new ProfileAssociationInput(1,[],[],[]),ProtectionFixture.Json);raw=kind switch {"unknown"=>raw.Insert(1,"\"actor\":null,"),"duplicate"=>raw.Insert(1,"\"expectedRevision\":1,"),_=>raw.Replace("\"expectedRevision\":1","\"expectedRevision\":\"1\"")};Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<ProfileAssociationInput>(raw,ProtectionFixture.Json));}
}
