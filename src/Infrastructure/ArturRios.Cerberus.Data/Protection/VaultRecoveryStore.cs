using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Protection;

public sealed class VaultRecoveryStore(IDbContextFactory<AppDbContext> factory) : IVaultRecoveryStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };

    public async Task<VaultResult<RecoveryDetails>> RecoverAsync(RecoveryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        var input = request.Replacement;
        if (input?.IsValid() != true || request.RawBody is not { Length: > 0 and <= 1048576 }) return new(Error: "validation_failed");
        var hash = ProtocolBinary.Encode(SHA256.HashData(request.RawBody));
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE", cancellationToken);
            var a = await db.Accounts.AsNoTracking().Where(x => x.HeimdallPublicId == request.Actor)
                .Select(x => new { x.Id, x.PublicId, x.State, x.PolicyRevision, x.RevocationGeneration, x.RenewalEnabled }).SingleOrDefaultAsync(cancellationToken);
            if (a is null || a.State != AccountState.Active || await db.TerminalErasures.AnyAsync(x => x.ResourceId == a.PublicId, cancellationToken)) return new(Error: "not_found");
            var now = await Now(db, cancellationToken);
            if (!Fresh(request.IdentityIssuedAt, now)) return new(Error: "fresh_authentication_required");
            var stored = await db.VaultRecoveryOperations.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == a.Id && x.IdempotencyKey == input.IdempotencyKey, cancellationToken);
            if (stored is not null)
            {
                if (stored.RequestHash != hash) return new(Error: "revision_conflict");
                if (!ProtocolBinary.TryDecode(stored.RequestHash, 32, out _) || stored.ProtectionRevision != input.ExpectedRevision + 1
                    || stored.ProtectionRevision > ProtocolBinary.MaxInteger || stored.Generation != input.RecoveryWrapper.Generation
                    || stored.RevocationGeneration is <= 0 or > ProtocolBinary.MaxInteger || !Matches(input, request.RawBody))
                    return new(Error: "persistence_unavailable");
                // Historical nonsecret result, before consumed/expired/current-state checks.
                return new(new("committed", stored.ProtectionRevision, stored.Generation, stored.RevocationGeneration));
            }
            var protection = await db.VaultProtections.SingleOrDefaultAsync(x => x.AccountId == a.Id, cancellationToken);
            if (protection is null) return new(Error: "not_found");
            var current = JsonSerializer.Deserialize<ProtectionMaterial>(protection.Material, Json);
            if (current?.IsValid() != true || protection.KeyEpoch != current.PasswordWrapper.KeyEpoch
                || protection.RecoveryGeneration != current.RecoveryWrapper.Generation || protection.Revision <= 0
                || a.PolicyRevision is <= 0 or > ProtocolBinary.MaxInteger || a.RevocationGeneration <= 0) return new(Error: "persistence_unavailable");
            if (protection.Revision >= ProtocolBinary.MaxInteger || protection.KeyEpoch >= ProtocolBinary.MaxInteger
                || protection.RecoveryGeneration >= ProtocolBinary.MaxInteger || a.RevocationGeneration >= ProtocolBinary.MaxInteger)
                return new(Error: "revision_conflict");
            if (input.Operation == "recover" && input.RecoveryWrapper.Generation <= protection.RecoveryGeneration) return new(Error: "recovery_credential_consumed");
            if (input.ExpectedRevision != protection.Revision) return new(Error: "revision_conflict");
            var recover = input.Operation == "recover";
            var accessVerifier = request.AccessVerifier ?? string.Empty;
            VaultAccessSession? session = null;
            if (!recover)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE account_id={a.Id} AND handle_verifier={accessVerifier} FOR UPDATE", cancellationToken);
                session = await db.VaultAccessSessions.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == a.Id && x.HandleVerifier == accessVerifier, cancellationToken);
            }
            bool Permitted(DateTimeOffset at) => recover || session is not null && session.ProfileId is null && !session.Revoked
                && session.PolicyRevision == a.PolicyRevision && session.RevocationGeneration == a.RevocationGeneration
                && session.IssuedAt <= at && (a.RenewalEnabled ? session.ExpiresAt > at : session.ExpiresAt is null);
            now = await Now(db, cancellationToken);
            if (!Fresh(request.IdentityIssuedAt, now)) return new(Error: "fresh_authentication_required");
            if (!Permitted(now)) return new(Error: "vault_access_denied");
            if (!input.IsValidTransition(current)) return new(Error: "validation_failed");
            var row = await db.VaultUnlockChallenges.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == a.Id && x.PublicId == request.ChallengeId, cancellationToken);
            if (row is null || row.Consumed) return new(Error: "vault_proof_rejected");
            var challenge = JsonSerializer.Deserialize<VaultProofChallenge>(row.Challenge, Json) ?? throw new JsonException();
            if (challenge.ChallengeId != row.PublicId || row.ExpiresAt != DateTimeOffset.FromUnixTimeSeconds(challenge.ExpiresAt)) return new(Error: "persistence_unavailable");
            if (row.PolicyRevision != a.PolicyRevision || row.RevocationGeneration != a.RevocationGeneration
                || challenge.ProtectionRevision != protection.Revision || challenge.KeyEpoch != protection.KeyEpoch
                || challenge.Operation == "recover" && challenge.Generation != protection.RecoveryGeneration) return new(Error: "revision_conflict");
            now = await Now(db, cancellationToken);
            if (!Fresh(request.IdentityIssuedAt, now)) return new(Error: "fresh_authentication_required");
            var binding = new VaultProofBinding(input.Operation, request.Actor, a.PublicId, "account", a.PublicId, protection.KeyEpoch, protection.Revision, recover ? protection.RecoveryGeneration : null);
            if (!Permitted(now)) return new(Error: "vault_access_denied");
            if (!VaultProof.Verify(challenge, binding, request.RawBody, recover ? current.RecoveryVerifier : current.UnlockVerifier, request.Proof, now.ToUnixTimeSeconds())
                || !Matches(input, request.RawBody)) return new(Error: "vault_proof_rejected");
            var consumed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cerberus.vault_unlock_challenge SET consumed=TRUE
                WHERE id={row.Id} AND NOT consumed AND expires_at>statement_timestamp()
                AND floor(extract(epoch from statement_timestamp()))>={request.IdentityIssuedAt}
                AND floor(extract(epoch from statement_timestamp()))<{request.IdentityIssuedAt}+60
                AND ({recover} OR EXISTS(SELECT 1 FROM cerberus.vault_access_session
                    WHERE account_id={a.Id} AND handle_verifier={accessVerifier}
                    AND profile_id IS NULL AND NOT revoked AND policy_revision={a.PolicyRevision}
                    AND revocation_generation={a.RevocationGeneration} AND issued_at<=statement_timestamp()
                    AND (({a.RenewalEnabled} AND expires_at>statement_timestamp())
                        OR (NOT {a.RenewalEnabled} AND expires_at IS NULL))))
                """, cancellationToken);
            if (consumed != 1)
            {
                now = await Now(db, cancellationToken);
                return new(Error: !Fresh(request.IdentityIssuedAt, now) ? "fresh_authentication_required" : !Permitted(now) ? "vault_access_denied" : "vault_proof_rejected");
            }
            protection.Material = JsonSerializer.SerializeToUtf8Bytes(input.Replace(current), Json);
            protection.Revision++; protection.KeyEpoch++; protection.RecoveryGeneration++;
            var changed = await db.Accounts.Where(x => x.Id == a.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.RevocationGeneration, x => x.RevocationGeneration + 1), cancellationToken);
            if (changed != 1) return new(Error: "revision_conflict");
            var result = new RecoveryDetails("committed", protection.Revision, protection.RecoveryGeneration, a.RevocationGeneration + 1);
            db.VaultRecoveryOperations.Add(new VaultRecoveryOperation { AccountId = a.Id, IdempotencyKey = input.IdempotencyKey,
                RequestHash = hash, ProtectionRevision = result.ProtectionRevision, Generation = result.Generation, RevocationGeneration = result.RevocationGeneration });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(result);
        }
        catch (DbUpdateConcurrencyException) { return new(Error: "revision_conflict"); }
        catch (Exception exception) when (exception is DbException or DbUpdateException or TimeoutException or JsonException or ArgumentOutOfRangeException
            || exception is InvalidOperationException { InnerException: DbUpdateException or DbException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
    }
    private static bool Matches(RecoveryReplacement input, byte[] raw)
    { try { return JsonSerializer.Deserialize<RecoveryReplacement>(raw, Json) == input; } catch (JsonException) { return false; } }
    private static bool Fresh(long issued, DateTimeOffset now) => issued >= 0 && issued <= now.ToUnixTimeSeconds() && now.ToUnixTimeSeconds() - issued < 60;
    private static Task<DateTimeOffset> Now(AppDbContext db, CancellationToken ct) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
}
