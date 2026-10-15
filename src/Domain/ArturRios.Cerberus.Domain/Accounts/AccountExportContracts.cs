using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Accounts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record AccountExportInput([property:JsonRequired,Range(1d,ProtocolBinary.MaxInteger)]long ExpectedRevision)
{
    public bool IsValid()=>ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger;
}

public sealed record AccountExportHeader(Guid AccountId,Guid IdentityId,long Revision,long PolicyRevision,long RevocationGeneration,
    bool RenewalEnabled,[property:JsonIgnore(Condition=JsonIgnoreCondition.Never)]long? RenewalIntervalSeconds,DateTimeOffset ServerTime)
{
    public bool IsValid(Guid actor,long expectedRevision)=>actor!=Guid.Empty&&AccountId!=Guid.Empty&&IdentityId==actor
        &&Revision==expectedRevision&&Revision is >0 and <=ProtocolBinary.MaxInteger
        &&PolicyRevision is >0 and <=ProtocolBinary.MaxInteger&&RevocationGeneration is >0 and <=ProtocolBinary.MaxInteger
        &&ServerTime.Offset==TimeSpan.Zero&&ServerTime>=DateTimeOffset.UnixEpoch&&ServerTime.Ticks%TimeSpan.TicksPerMicrosecond==0
        &&(RenewalEnabled?RenewalIntervalSeconds is >0 and <=ProtocolBinary.MaxInteger:RenewalIntervalSeconds is null);
}
public sealed record AccountExportRequest(Guid Actor,string AccessVerifier,long AuthenticationIssuedAt,long ExpectedRevision);
public sealed record AccountExportOpenResult(IAccountExportLease? Lease=null,string? Error=null);
public sealed record AccountExportArchive(string Format,AccountExportHeader Header,IReadOnlyList<AccountExportEntry> Entries,long EntryCount,bool Complete);
public interface IAccountExportStore
{
    Task<AccountExportOpenResult> OpenAsync(AccountExportRequest request,CancellationToken cancellationToken);
}
public interface IAccountExportLease:IAsyncDisposable
{
    AccountExportHeader Header{get;}
    IAsyncEnumerable<AccountExportEntry> ReadAsync(CancellationToken cancellationToken);
}

public sealed class AccountExportException(string error):Exception("Account export is unavailable.")
{
    public string Error{get;}=error;
}
