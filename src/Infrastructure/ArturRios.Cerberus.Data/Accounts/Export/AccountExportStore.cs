using ArturRios.Cerberus.Domain.Accounts;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Accounts.Export;

public sealed class AccountExportStore(IDbContextFactory<AppDbContext> factory):IAccountExportStore
{
    public async Task<AccountExportOpenResult> OpenAsync(AccountExportRequest request,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(!new AccountExportInput(request.ExpectedRevision).IsValid())return new(Error:"validation_failed");
        AppDbContext? db=null;AccountExportLease? lease=null;
        try
        {
            db=await factory.CreateDbContextAsync(cancellationToken);
            var tx=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken);
            lease=new(db,tx,request);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY",cancellationToken);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL TIME ZONE 'UTC'",cancellationToken);
            await db.Database.ExecuteSqlRawAsync(AccountExportSql.Declare,AccountExportSql.Parameters(request,true),cancellationToken);
            await lease.InitializeAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var result=new AccountExportOpenResult(Lease:lease);lease=null;db=null;return result;
        }
        catch(AccountExportException e){return new(Error:e.Error);}
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e) when(e is DbException or TimeoutException or JsonException or FormatException or InvalidOperationException)
        {return new(Error:"persistence_unavailable");}
        finally
        {
            // Failed admission never transfers ownership, including cancellation at handoff.
            if(lease is not null){try{await lease.DisposeAsync();}catch(Exception){/* Safe failure result already chosen. */}}
            else if(db is not null){try{await db.DisposeAsync();}catch(Exception){/* No lease escaped. */}}
        }
    }
}
