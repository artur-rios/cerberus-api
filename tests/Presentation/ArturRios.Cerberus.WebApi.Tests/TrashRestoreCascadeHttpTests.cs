using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.WebApi.Tests;

public partial class TrashRestoreHttpTests
{
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenFolderCascadeAndIndependentPriorTrash_WhenRestoring_ThenOnlyChosenSurvivorsAndSafeOmissions(bool restoreIndependent)
    {
        using var keys=new ArturRios.Cerberus.TestSupport.ProtectionFixture();var s=await Setup(keys);var folder=Guid.NewGuid();var active=Guid.NewGuid();var prior=Guid.NewGuid();
        using(var r=await Send(s,"/api/folders",HttpMethod.Post,Bytes(new{folderId=folder,envelope=Envelope(),editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),parentFolderId=(Guid?)null})))Assert.Equal(HttpStatusCode.Created,r.StatusCode);
        foreach(var id in new[]{active,prior})using(var r=await Send(s,"/api/records",HttpMethod.Post,Bytes(new{recordId=id,envelope=Envelope(),editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),folderId=folder})))Assert.Equal(HttpStatusCode.Created,r.StatusCode);
        using var priorDeleted=await Send(s,"/api/records/"+prior,HttpMethod.Delete,Bytes(new{expectedRevision=1}));var independent=await Success(priorDeleted);
        using var folderRead=await Send(s,"/api/folders/"+folder);var original=await Success(folderRead);
        using var cascadeDeleted=await Send(s,"/api/folders/"+folder,HttpMethod.Delete,Bytes(new{expectedRevision=original.GetProperty("revision").GetInt64()}));var cascade=await Success(cascadeDeleted);
        var chosen=restoreIndependent?independent:cascade;
        using var restored=await Send(s,"/api/trash/"+chosen.GetProperty("trashOperationId").GetGuid()+"/restore",HttpMethod.Post,Bytes(new{expectedRevision=chosen.GetProperty("revision").GetInt64()}));var data=await Success(restored);
        var ids=data.GetProperty("items").EnumerateArray().Select(x=>x.GetProperty("resourceId").GetGuid()).ToArray();
        Assert.Equal(restoreIndependent?new[]{prior}.Order():new[]{folder,active}.Order(),ids.Order());
        if(restoreIndependent){var omission=Assert.Single(data.GetProperty("omissions").EnumerateArray());Fields(omission,"resourceKind","resourceId","associationKind","count");Assert.Equal(prior,omission.GetProperty("resourceId").GetGuid());Assert.Equal("parent",omission.GetProperty("associationKind").GetString());Assert.Equal(1,omission.GetProperty("count").GetInt32());Assert.DoesNotContain(folder.ToString(),data.GetRawText());}
        else Assert.Empty(data.GetProperty("omissions").EnumerateArray());
        await using var db=fixture.Context();var previous=await db.Records.AsNoTracking().SingleAsync(x=>x.PublicId==prior);var member=await db.Records.AsNoTracking().SingleAsync(x=>x.PublicId==active);
        Assert.Equal(!restoreIndependent,previous.DeletedAt is not null);Assert.Equal(restoreIndependent,member.DeletedAt is not null);
        var other=restoreIndependent?cascade:independent;Assert.True(await db.TrashOperations.AnyAsync(x=>x.PublicId==other.GetProperty("trashOperationId").GetGuid()));
        if(Environment.GetEnvironmentVariable("CERBERUS_CAPTURE_UC51_JSON")=="1")await File.WriteAllTextAsync("/tmp/cerberus-uc51-http-"+(restoreIndependent?"omission":"cascade")+".json",data.GetRawText());
    }
}
