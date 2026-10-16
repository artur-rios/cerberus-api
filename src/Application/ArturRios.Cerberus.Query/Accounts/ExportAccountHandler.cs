using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;

namespace ArturRios.Cerberus.Query.Accounts;

public sealed class ExportAccountHandler(IAccountExportStore store):IQueryHandlerAsync<ExportAccountQuery,AccountExportOutput>
{
    public async Task<DataOutput<AccountExportOutput?>> HandleAsync(ExportAccountQuery query,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output=DataOutput<AccountExportOutput?>.New;
        if(query.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(!new AccountExportInput(query.ExpectedRevision).IsValid())return output.WithError("validation_failed");
        if(query.Access is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(query.Access,out var verifier))return output.WithError("validation_failed");
        if(query.AuthenticationIssuedAt<=0)return output.WithError("fresh_authentication_required");
        var result=await store.OpenAsync(new(query.Actor,verifier,query.AuthenticationIssuedAt,query.ExpectedRevision),cancellationToken);
        var lease=result?.Lease;var published=false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(result is null||result.Error is not null||lease is null)
                return output.WithError(lease is null&&result?.Error is {} error&&AccountExportMessages.StatusCodes.ContainsKey(error)
                    ?error:"persistence_unavailable");
            var header=lease.Header;
            if(header is null||!header.IsValid(query.Actor,query.ExpectedRevision)
                ||!FreshAuthentication.IsFresh(query.AuthenticationIssuedAt,header.ServerTime))return output.WithError("persistence_unavailable");
            var ready=output.WithData(new AccountExportOutput{Lease=lease}).WithMessage("account_export_ready");
            published=true;return ready;
        }
        finally
        {
            if(!published)
            {
                if(lease is not null)await DiscardAsync(lease);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
    private static async Task DiscardAsync(IAccountExportLease lease)
    {
        try{await lease.DisposeAsync();}
        catch(Exception){/* Rejected producer/cleanup details never replace the safe unavailable result. */}
    }
}
