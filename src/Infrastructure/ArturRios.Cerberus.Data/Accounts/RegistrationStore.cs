using System.Data.Common;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Accounts;

public sealed class RegistrationStore(IDbContextFactory<AppDbContext> factory) : IRegistrationStore
{
    public async Task<RegistrationResult> RegisterAsync(RegistrationRequest request,
        Func<CancellationToken, Task<RegistrationIdentity>> establishIdentity, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            // Commit the operation before the external side effect. A later rollback must
            // leave enough metadata to reconcile using the client's resubmitted credentials.
            await using (var prepare = await context.Database.BeginTransactionAsync(cancellationToken))
            {
                await LockAsync(context, request.IdentityLock, cancellationToken);
                if (await ErasedAsync(context, request.AccountId, cancellationToken)) return new(Error: "not_found");
                var existing = await context.RegistrationOperations.SingleOrDefaultAsync(x => x.OperationId == request.OperationId, cancellationToken);
                if (existing is not null && (existing.RequestFingerprint != request.RequestFingerprint || existing.AccountPublicId != request.AccountId))
                    return new(Error: "registration_conflict");
                if (existing is null)
                {
                    context.RegistrationOperations.Add(new RegistrationOperation
                    {
                        OperationId = request.OperationId, AccountPublicId = request.AccountId,
                        RequestFingerprint = request.RequestFingerprint
                    });
                    await context.SaveChangesAsync(cancellationToken);
                }
                await prepare.CommitAsync(cancellationToken);
            }
            context.ChangeTracker.Clear();
            return await CompleteAsync(context, request, establishIdentity, cancellationToken);
        }
        catch (DbUpdateConcurrencyException) { return new(Error: "registration_conflict"); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } duplicate)
        {
            return new(Error: duplicate.ConstraintName == "ix_account_public_id" ? "not_found" : "registration_conflict");
        }
        catch (DbUpdateException) { return new(Error: "persistence_unavailable"); }
        catch (DbException) { return new(Error: "persistence_unavailable"); }
        // Npgsql's non-retrying EF strategy wraps transient provider errors.
        // Surface the retryable outcome without replaying the external side effect.
        catch (InvalidOperationException exception) when (exception.InnerException is DbUpdateException or DbException or TimeoutException)
        { return new(Error: "persistence_unavailable"); }
        catch (TimeoutException) { return new(Error: "persistence_unavailable"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
    }

    private static async Task<RegistrationResult> CompleteAsync(AppDbContext context, RegistrationRequest request,
        Func<CancellationToken, Task<RegistrationIdentity>> establishIdentity, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(context, request.IdentityLock, cancellationToken);
        var operations = await context.RegistrationOperations.FromSqlInterpolated(
            $"SELECT * FROM cerberus.registration_operation WHERE operation_id = {request.OperationId} FOR UPDATE").ToListAsync(cancellationToken);
        var operation = operations.Single();
        if (await ErasedAsync(context, request.AccountId, cancellationToken)) return new(Error: "not_found");
        var identity = await establishIdentity(cancellationToken);
        if (identity.Status != RegistrationIdentityStatus.Verified)
            return new(Error: identity.Status switch
            {
                RegistrationIdentityStatus.Denied => "identity_proof_required",
                RegistrationIdentityStatus.Forbidden => "registration_forbidden",
                RegistrationIdentityStatus.NotFound => "not_found",
                _ => "identity_unavailable"
            });
        if (identity.IdentityId is null || identity.IdentityId == Guid.Empty) return new(Error: "identity_unavailable");
        if (operation.CompletedIdentityId is not null && operation.CompletedIdentityId != identity.IdentityId) return new(Error: "not_found");
        if (await ErasedAsync(context, request.AccountId, cancellationToken)) return new(Error: "not_found");
        var account = await context.Accounts.SingleOrDefaultAsync(x => x.HeimdallPublicId == identity.IdentityId, cancellationToken);
        if (account is not null)
        {
            if (operation.CompletedIdentityId is null || account.PublicId != request.AccountId) return new(Error: "registration_conflict");
            if (account.State != AccountState.Active) return new(Error: "authentication_required");
            return new(account.PublicId, 1, Replayed: true);
        }
        // A completed operation whose account has disappeared must never recreate it.
        if (operation.CompletedIdentityId is not null || await context.Accounts.AnyAsync(x => x.PublicId == request.AccountId, cancellationToken))
            return new(Error: "not_found");
        context.Accounts.Add(new Account { PublicId = request.AccountId, HeimdallPublicId = identity.IdentityId.Value, DetailsEnvelope = request.DetailsEnvelope });
        operation.CompletedIdentityId = identity.IdentityId;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(request.AccountId, 1);
    }

    private static Task LockAsync(AppDbContext context, long identityLock, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({identityLock})", cancellationToken);
    private static Task<bool> ErasedAsync(AppDbContext context, Guid accountId, CancellationToken cancellationToken) =>
        context.TerminalErasures.AnyAsync(x => x.ResourceId == accountId, cancellationToken);
}
