using System.Text.Json;
namespace ArturRios.Cerberus.Domain.Trash;

public sealed record TrashAssociations(Guid? Parent,Guid[] ProfileIds,Guid[] CollectionIds,Guid[] RecordIds,Guid[] FolderIds)
{
    public static bool TryRead(string kind,byte[]? bytes,out TrashAssociations? data)
    {
        data=null;
        if(bytes is null || kind is not ("record" or "folder" or "collection" or "profile"))return false;
        try
        {
            using var document=JsonDocument.Parse(bytes);var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)return false;
            var parentName=kind=="record"?"folderId":"parentFolderId";
            var names=kind switch{
                "profile"=>new[]{"recordIds","folderIds","collectionIds"},
                "collection"=>new[]{"profileIds","recordIds","folderIds"},
                _=>new[]{parentName,"profileIds","collectionIds"}};
            var fields=root.EnumerateObject().ToArray();
            if(fields.Length!=3 || fields.Select(x=>x.Name).Distinct(StringComparer.Ordinal).Count()!=3
                || fields.Any(x=>!names.Contains(x.Name,StringComparer.Ordinal)))return false;
            var profiles=kind=="profile"?[]:Ids(root.GetProperty("profileIds"));
            var collections=kind=="collection"?[]:Ids(root.GetProperty("collectionIds"));
            var records=kind is "profile" or "collection"?Ids(root.GetProperty("recordIds")):[];
            var folders=kind is "profile" or "collection"?Ids(root.GetProperty("folderIds")):[];
            Guid? parent=null;
            if(kind is "record" or "folder"){
                var value=root.GetProperty(parentName);parent=value.ValueKind==JsonValueKind.Null?null:Id(value);}
            data=new(parent,profiles,collections,records,folders);return true;
        }
        catch(JsonException){return false;}
    }
    private static Guid Id(JsonElement value)=>value.ValueKind==JsonValueKind.String
        && Guid.TryParseExact(value.GetString(),"D",out var id) && id!=Guid.Empty && value.GetString()==id.ToString("D")
            ?id:throw new JsonException("Invalid retained identity.");
    private static Guid[] Ids(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Array)throw new JsonException("Invalid retained associations.");
        var ids=value.EnumerateArray().Select(Id).ToArray();
        if(ids.Distinct().Count()!=ids.Length)throw new JsonException("Duplicate retained identity.");
        return ids;
    }
}
