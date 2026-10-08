using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Protection;

public sealed class VaultProtectionStore(IDbContextFactory<AppDbContext> factory) : IVaultProtectionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };

    public Task<VaultResult<VaultProtectionDetails>> InitializeAsync(Guid actor, Guid accountId, long expectedAccountRevision,
        ProtectionMaterial material, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (material?.IsValid() != true || material.PasswordWrapper.KeyEpoch != 1 || material.RecoveryWrapper.Generation != 1
            || expectedAccountRevision is <= 0 or > ProtocolBinary.MaxInteger || accountId == Guid.Empty)
            return Task.FromResult(new VaultResult<VaultProtectionDetails>(Error: "validation_failed"));
        return Run<VaultProtectionDetails>(actor, cancellationToken, async (db, account, ct) =>
        {
            if (account.PublicId != accountId) return new(Error: "not_found");
            if (account.Revision != expectedAccountRevision || await db.VaultProtections.AnyAsync(x => x.AccountId == account.Id, ct))
                return new(Error: "revision_conflict");
            db.VaultProtections.Add(new VaultProtection { AccountId = account.Id, Material = JsonSerializer.SerializeToUtf8Bytes(material, Json) });
            return new(new(account.PublicId, 1, 1, 1, material));
        });
    }

    public Task<VaultResult<VaultProtectionDetails>> ReadAsync(Guid actor, CancellationToken cancellationToken) =>
        Run<VaultProtectionDetails>(actor, cancellationToken, async (db, account, ct) =>
        {
            var row = await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id, ct);
            return row is null ? new(Error: "not_found") : new(Details(account, row));
        });

    public Task<VaultResult<VaultProofChallenge>> ChallengeAsync(Guid actor, string requestHash, CancellationToken cancellationToken)
        => ChallengeAsync(actor, "unlock-account", requestHash, cancellationToken);

    public Task<VaultResult<VaultProofChallenge>> ChallengeAsync(Guid actor, string operation, string requestHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation is not ("unlock-account" or "change-protection") || !ProtocolBinary.TryDecode(requestHash, 32, out _)) return Task.FromResult(new VaultResult<VaultProofChallenge>(Error: "validation_failed"));
        return Run<VaultProofChallenge>(actor, cancellationToken, async (db, account, ct) =>
        {
            var row = await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id, ct);
            if (row is null) return new(Error: "not_found");
            _ = Details(account, row);
            if (account.PolicyRevision <= 0 || account.RevocationGeneration <= 0) return new(Error: "persistence_unavailable");
            var now = await Now(db, ct);
            await db.VaultUnlockChallenges.Where(x => x.AccountId == account.Id && (x.Consumed || x.ExpiresAt <= now)).ExecuteDeleteAsync(ct);
            var issued = now.ToUnixTimeSeconds();
            var challenge = new VaultProofChallenge("cerberus-challenge-v1", Guid.NewGuid(), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)),
                operation, actor, account.PublicId, "account", account.PublicId, row.KeyEpoch, row.Revision, null, requestHash, issued, issued + 60);
            db.VaultUnlockChallenges.Add(new VaultUnlockChallenge { AccountId = account.Id, PublicId = challenge.ChallengeId,
                Challenge = JsonSerializer.SerializeToUtf8Bytes(challenge, Json), PolicyRevision = account.PolicyRevision,
                RevocationGeneration = account.RevocationGeneration, ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(challenge.ExpiresAt) });
            return new(challenge);
        });
    }

    public Task<VaultResult<VaultAccessDetails>> UnlockAsync(Guid actor, long expectedProtectionRevision, Guid challengeId,
        string proof, byte[] rawBody, CancellationToken cancellationToken) =>
        Run<VaultAccessDetails>(actor, cancellationToken, async (db, account, ct) =>
        {
            var protection = await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id, ct);
            if (protection is null) return new(Error: "not_found");
            var details = Details(account, protection);
            if (expectedProtectionRevision != protection.Revision) return new(Error: "revision_conflict");
            var row = await db.VaultUnlockChallenges.AsNoTracking().SingleOrDefaultAsync(x => x.PublicId == challengeId && x.AccountId == account.Id, ct);
            if (row is null || row.Consumed) return new(Error: "vault_proof_rejected");
            var challenge = JsonSerializer.Deserialize<VaultProofChallenge>(row.Challenge, Json) ?? throw new JsonException();
            if (challenge.ChallengeId != row.PublicId || row.ExpiresAt != DateTimeOffset.FromUnixTimeSeconds(challenge.ExpiresAt))
                return new(Error: "persistence_unavailable");
            if (row.PolicyRevision != account.PolicyRevision || row.RevocationGeneration != account.RevocationGeneration
                || challenge.ProtectionRevision != protection.Revision || challenge.KeyEpoch != protection.KeyEpoch)
                return new(Error: "revision_conflict");
            var now = await Now(db, ct);
            var binding = new VaultProofBinding("unlock-account", actor, account.PublicId, "account", account.PublicId, protection.KeyEpoch, protection.Revision, null);
            if (!VaultProof.Verify(challenge, binding, rawBody, details.Material.UnlockVerifier, proof, now.ToUnixTimeSeconds()))
                return new(Error: "vault_proof_rejected");
            if (account.PolicyRevision <= 0 || account.RevocationGeneration <= 0
                || account.RenewalEnabled && account.RenewalInterval is not { Ticks: > 0 }) return new(Error: "persistence_unavailable");
            DateTimeOffset? expires = account.RenewalEnabled ? now.Add(account.RenewalInterval!.Value) : null;
            var access = ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32));
            if (!OpaqueAccessHandle.TryHash(access, out var verifier)) return new(Error: "persistence_unavailable");
            // Recheck exclusive expiry at the consumption statement, not transaction begin.
            var consumed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cerberus.vault_unlock_challenge SET consumed = TRUE
                WHERE id = {row.Id} AND NOT consumed AND expires_at > statement_timestamp()
                """, ct);
            if (consumed != 1) return new(Error: "vault_proof_rejected");
            db.VaultAccessSessions.Add(new VaultAccessSession { AccountId = account.Id, HandleVerifier = verifier,
                IssuedAt = now, ExpiresAt = expires, PolicyRevision = account.PolicyRevision, RevocationGeneration = account.RevocationGeneration });
            return new(new(account.PublicId, access, now, expires));
        });

    private async Task<VaultResult<T>> Run<T>(Guid actor, CancellationToken cancellationToken,
        Func<AppDbContext, AccountView, CancellationToken, Task<VaultResult<T>>> execute) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (actor == Guid.Empty) return new(Error: "authentication_required");
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE heimdall_public_id = {actor} FOR UPDATE", cancellationToken);
            // Project permission metadata only; never retrieve the account ciphertext.
            var account = await db.Accounts.AsNoTracking().Where(x => x.HeimdallPublicId == actor)
                .Select(x => new AccountView(x.Id, x.PublicId, x.Revision, x.State, x.RenewalEnabled, x.RenewalInterval, x.PolicyRevision, x.RevocationGeneration))
                .SingleOrDefaultAsync(cancellationToken);
            if (account is null || account.State != AccountState.Active
                || await db.TerminalErasures.AnyAsync(x => x.ResourceId == account.PublicId, cancellationToken)) return new(Error: "not_found");
            var result = await execute(db, account, cancellationToken);
            if (result.Error is not null) return result;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateConcurrencyException) { return new(Error: "revision_conflict"); }
        catch (Exception exception) when (exception is DbException or DbUpdateException or TimeoutException or JsonException or ArgumentOutOfRangeException
            || exception is InvalidOperationException { InnerException: DbUpdateException or DbException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
    }

    private static VaultProtectionDetails Details(AccountView account, VaultProtection row)
    {
        var material = JsonSerializer.Deserialize<ProtectionMaterial>(row.Material, Json);
        if (material?.IsValid() != true || row.Revision is <= 0 or > ProtocolBinary.MaxInteger
            || row.KeyEpoch != material.PasswordWrapper.KeyEpoch || row.RecoveryGeneration != material.RecoveryWrapper.Generation) throw new JsonException();
        return new(account.PublicId, row.Revision, row.KeyEpoch, row.RecoveryGeneration, material);
    }
    private static Task<DateTimeOffset> Now(AppDbContext db, CancellationToken ct) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
    private sealed record AccountView(long Id, Guid PublicId, long Revision, AccountState State, bool RenewalEnabled,
        TimeSpan? RenewalInterval, long PolicyRevision, long RevocationGeneration);
}
