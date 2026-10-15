using System.Text;
using ArturRios.Cerberus.Domain.Trash;
namespace ArturRios.Cerberus.Domain.Tests;

public class TrashAssociationsTests
{
    private const string P="aaaaaaaa-0000-0000-0000-000000000001";
    private const string C="bbbbbbbb-0000-0000-0000-000000000002";
    private const string F="cccccccc-0000-0000-0000-000000000003";
    private const string R="dddddddd-0000-0000-0000-000000000004";
    [UnitTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public void GivenTypedOriginalAssociations_WhenDecoding_ThenPreserveExactTypedIdentifiers(string kind)
    {
        var bytes=Encoding.UTF8.GetBytes(Json(kind));Assert.True(TrashAssociations.TryRead(kind,bytes,out var data));
        Assert.Equal(kind is "record" or "folder"?Guid.Parse(F):null,data!.Parent);
        Assert.Equal(kind=="profile"?Array.Empty<Guid>():[Guid.Parse(P)],data.ProfileIds);
        Assert.Equal(kind=="collection"?Array.Empty<Guid>():[Guid.Parse(C)],data.CollectionIds);
        Assert.Equal(kind is "profile" or "collection"?[Guid.Parse(R)]:Array.Empty<Guid>(),data.RecordIds);
        Assert.Equal(kind is "profile" or "collection"?[Guid.Parse(F)]:Array.Empty<Guid>(),data.FolderIds);
    }
    [UnitTheory][InlineData("record")][InlineData("folder")]
    public void GivenNullFormerParent_WhenDecoding_ThenRetainDirectAssociations(string kind)
    {Assert.True(TrashAssociations.TryRead(kind,Encoding.UTF8.GetBytes(Json(kind).Replace('"'+F+'"',"null")),out var data));Assert.Null(data!.Parent);Assert.Equal([Guid.Parse(P)],data.ProfileIds);}
    [UnitTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public void GivenEmptyAssociations_WhenDecoding_ThenValidEmptyTypedSnapshot(string kind)
    {var json=Json(kind).Replace('"'+P+'"',"").Replace('"'+C+'"',"").Replace('"'+R+'"',"");if(kind is "collection" or "profile")json=json.Replace('"'+F+'"',"");else json=json.Replace('"'+F+'"',"null");Assert.True(TrashAssociations.TryRead(kind,Encoding.UTF8.GetBytes(json),out var data));Assert.Empty(data!.ProfileIds);Assert.Empty(data.CollectionIds);Assert.Empty(data.RecordIds);Assert.Empty(data.FolderIds);}
    [UnitTheory][InlineData("badJson")][InlineData("null")][InlineData("array")][InlineData("unknown")][InlineData("duplicate")]
    [InlineData("missing")][InlineData("case")][InlineData("nullArray")][InlineData("number")][InlineData("duplicateId")]
    [InlineData("upper")][InlineData("emptyId")][InlineData("compactId")][InlineData("parentNumber")][InlineData("nullId")]
    public void GivenMalformedRetainedAuthority_WhenDecoding_ThenFailWithoutPartialAuthority(string damage)
    {
        var json=Json("record");json=damage switch{
            "badJson"=>"bad","null"=>"null","array"=>"[]","unknown"=>json.Replace("{","{\"extra\":[],"),
            "duplicate"=>json.Replace("{","{\"profileIds\":[],"),"missing"=>json.Replace(",\"collectionIds\":[\""+C+"\"]", ""),
            "case"=>json.Replace("profileIds","ProfileIds"),"nullArray"=>json.Replace("[\""+P+"\"]","null"),
            "number"=>json.Replace('"'+P+'"',"1"),"duplicateId"=>json.Replace('"'+P+'"','"'+P+"\",\""+P+'"'),
            "upper"=>json.Replace(P,P.ToUpperInvariant()),"emptyId"=>json.Replace(P,Guid.Empty.ToString()),
            "compactId"=>json.Replace(P,Guid.Parse(P).ToString("N")),"parentNumber"=>json.Replace('"'+F+'"',"1"),
            "nullId"=>json.Replace('"'+P+'"',"null"),_=>json};
        Assert.False(TrashAssociations.TryRead("record",Encoding.UTF8.GetBytes(json),out var data));Assert.Null(data);
    }
    [UnitTheory][InlineData("grant")][InlineData("")][InlineData("Record")]
    public void GivenUnsupportedKind_WhenDecoding_ThenNoAuthority(string kind)
    {Assert.False(TrashAssociations.TryRead(kind,Encoding.UTF8.GetBytes(Json("record")),out var data));Assert.Null(data);}
    [UnitFact]
    public void GivenInvalidUtf8_WhenDecoding_ThenNoAuthority()
    {Assert.False(TrashAssociations.TryRead("record",[0xff,0xfe],out var data));Assert.Null(data);}
    [UnitFact]
    public void GivenSameGuidAcrossTypedKinds_WhenDecoding_ThenKeepIndependentTypedReferences()
    {Assert.True(TrashAssociations.TryRead("record",Encoding.UTF8.GetBytes(Json("record").Replace(C,P)),out var data));Assert.Equal([Guid.Parse(P)],data!.ProfileIds);Assert.Equal([Guid.Parse(P)],data.CollectionIds);}
    private static string Json(string kind)=>kind switch{
        "record"=>"{\"folderId\":\""+F+"\",\"profileIds\":[\""+P+"\"],\"collectionIds\":[\""+C+"\"]}",
        "folder"=>"{\"parentFolderId\":\""+F+"\",\"profileIds\":[\""+P+"\"],\"collectionIds\":[\""+C+"\"]}",
        "collection"=>"{\"profileIds\":[\""+P+"\"],\"recordIds\":[\""+R+"\"],\"folderIds\":[\""+F+"\"]}",
        _=>"{\"recordIds\":[\""+R+"\"],\"folderIds\":[\""+F+"\"],\"collectionIds\":[\""+C+"\"]}"};
}
