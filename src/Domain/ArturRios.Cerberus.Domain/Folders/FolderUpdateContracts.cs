using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Folders;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record FolderUpdateInput(
    [property:JsonRequired] long ExpectedRevision,
    [property:JsonRequired] EncryptedEnvelope Envelope,
    [property:JsonRequired] DateTimeOffset EditedAt)
{
    public bool IsValid()=>ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger && Envelope?.IsValid()==true
        && EditedAt.Offset==TimeSpan.Zero && EditedAt.Ticks>=TimeSpan.TicksPerMicrosecond;
}
public sealed record FolderUpdateRequest(Guid Actor,string AccessVerifier,Guid FolderId,FolderUpdateInput Input);
public interface IFolderUpdateStore
{
    Task<VaultResult<FolderCreateDetails>> UpdateAsync(FolderUpdateRequest request,CancellationToken cancellationToken);
}
