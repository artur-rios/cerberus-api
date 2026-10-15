using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Erasure;

public sealed class AccountPurgeHandler(IDbContextFactory<AppDbContext> factory,IErasureLedger ledger):IRetentionHandler
{
    public string Kind=>"account-erase";
    public async Task ExecuteAsync(RetentionClaim claim,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();var parts=claim.OperationKey.Split('/');
        if(parts.Length!=2||parts[0]!=Kind||!Guid.TryParseExact(parts[1],"D",out var id)||id==Guid.Empty||parts[1]!=id.ToString("D"))throw new InvalidOperationException("Invalid account erasure work.");
        await AccountErasureExecution.ExecuteAsync(factory,ledger,claim,id,cancellationToken);
    }
}
