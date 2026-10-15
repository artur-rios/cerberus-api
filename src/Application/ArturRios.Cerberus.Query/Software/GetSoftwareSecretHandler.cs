using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;

namespace ArturRios.Cerberus.Query.Software;

public sealed class GetSoftwareSecretHandler(ISoftwareSecretStore store,IHeimdallApplicationClient applications,CerberusOptions options,TimeProvider clock):IQueryHandlerAsync<GetSoftwareSecretQuery,SoftwareSecretOutput>
{
    public async Task<DataOutput<SoftwareSecretOutput?>> HandleAsync(GetSoftwareSecretQuery query,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();var output=DataOutput<SoftwareSecretOutput?>.New;
        var identity=query.Identity;
        if(identity?.IsValid()!=true||identity.ScopeId!=options.HeimdallScopeId||identity.ExpiresAt<=clock.GetUtcNow()||string.IsNullOrWhiteSpace(query.Bearer))
            return output.WithError("authentication_required");
        if(query.Input?.IsValid()!=true)return output.WithError("validation_failed");
        HeimdallApplicationAuthentication? current;
        try{current=await applications.AuthenticateAsync(query.Bearer,cancellationToken);}
        catch(OperationCanceledException)when(!cancellationToken.IsCancellationRequested){return output.WithError("identity_unavailable");}
        if(current?.Error is not null)
            return output.WithError(current.Identity is null&&current.Error=="authentication_required"?"authentication_required":"identity_unavailable");
        if(current?.Identity!=identity)return output.WithError("identity_unavailable");
        if(identity.ExpiresAt<=clock.GetUtcNow())return output.WithError("authentication_required");
        VaultResult<SoftwareSecretDetails>? result;
        try{result=await store.ReadAsync(new(identity,query.Input),cancellationToken);}
        catch(OperationCanceledException)when(!cancellationToken.IsCancellationRequested){return output.WithError("persistence_unavailable");}
        if(result?.Error is not null)return output.WithError(result.Data is not null?"persistence_unavailable":SoftwareSecretMessages.SafeStoreError(result.Error));
        var data=result?.Data;
        if(data?.IsValid(identity,query.Input)!=true)return output.WithError("persistence_unavailable");
        if(identity.ExpiresAt<=clock.GetUtcNow())return output.WithError("authentication_required");
        return output.WithData(new SoftwareSecretOutput
        {
            OwnerAccountId=data.OwnerAccountId,GrantId=data.GrantId,GrantRevision=data.GrantRevision,
            Target=data.Target,Provisioning=data.Provisioning,RecipientKey=data.RecipientKey,AuthorKey=data.AuthorKey
        }).WithMessage("software_secret_found");
    }
}
