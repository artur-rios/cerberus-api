using System.Data.Common;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Identity;

public sealed class IdentityUpdateStore(IDbContextFactory<AppDbContext> factory) : IIdentityUpdateStore
{
    public async Task<IdentityUpdateResult> UpdateAsync(Guid identityId, string verifier,
        Func<CancellationToken, Task<IdentityUpdateResult>> update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            // Lock order is account, then session. These selects retrieve no vault
            // payload and keep lifecycle/revocation writes outside the callback.
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM cerberus.account WHERE heimdall_public_id = {identityId} FOR UPDATE", cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT 1 FROM cerberus.vault_access_session AS s
                JOIN cerberus.account AS a ON a.id = s.account_id
                WHERE a.heimdall_public_id = {identityId} AND s.handle_verifier = {verifier}
                FOR UPDATE OF s
                """, cancellationToken);
            // now() is transaction-begin time and can expire while waiting for
            // locks. Authorize using a fresh statement timestamp after both locks.
            var decision = await context.Database.SqlQuery<int>($"""
                SELECT CASE
                  WHEN NOT EXISTS (
                    SELECT 1 FROM cerberus.account AS a
                    WHERE a.heimdall_public_id = {identityId} AND a.state = {(int)AccountState.Active}
                      AND NOT EXISTS (SELECT 1 FROM cerberus.terminal_erasure AS e WHERE e.resource_id = a.public_id)
                  ) THEN 404
                  WHEN NOT EXISTS (
                    SELECT 1 FROM cerberus.vault_access_session AS s
                    JOIN cerberus.account AS a ON a.id = s.account_id
                    WHERE a.heimdall_public_id = {identityId} AND s.handle_verifier = {verifier}
                      AND s.profile_id IS NULL AND NOT s.revoked AND s.issued_at <= statement_timestamp()
                      AND s.policy_revision > 0 AND s.revocation_generation > 0
                      AND s.policy_revision = a.policy_revision AND s.revocation_generation = a.revocation_generation
                      AND ((a.renewal_enabled AND s.expires_at > statement_timestamp())
                        OR (NOT a.renewal_enabled AND s.expires_at IS NULL))
                  ) THEN 403 ELSE 200 END AS "Value"
                """).SingleAsync(cancellationToken);
            if (decision != 200) return new(Error: decision switch
            { 404 => "not_found", 403 => "vault_access_denied", _ => "persistence_unavailable" });
            var result = await update(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is DbException or TimeoutException
            || exception is InvalidOperationException { InnerException: DbException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(Error: "persistence_unavailable"); }
    }
}
