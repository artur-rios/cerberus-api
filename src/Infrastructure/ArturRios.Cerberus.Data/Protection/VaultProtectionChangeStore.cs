using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Profiles;
using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Protection;

public sealed class VaultProtectionChangeStore(IDbContextFactory<AppDbContext> factory) : IVaultProtectionChangeStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };

    public async Task<VaultResult<ProtectionChangeDetails>> ChangeAsync(ProtectionChangeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        var input = request.Change;
        if (input?.IsValid() != true) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE", cancellationToken);
            var a = await db.Accounts.AsNoTracking().Where(x => x.HeimdallPublicId == request.Actor)
                .Select(x => new { x.Id, x.PublicId, x.Revision, x.State, x.PolicyRevision, x.RevocationGeneration, x.RenewalEnabled }).SingleOrDefaultAsync(cancellationToken);
            if (a is null || a.PublicId != input.AccountId || a.State != AccountState.Active
                || await db.TerminalErasures.AnyAsync(x => x.ResourceId == a.PublicId, cancellationToken)) return new(Error: "not_found");
            var metadata = await db.VaultProtections.AsNoTracking().Where(x => x.AccountId == a.Id)
                .Select(x => new { x.Id, x.Revision, x.KeyEpoch, x.RecoveryGeneration }).SingleOrDefaultAsync(cancellationToken);
            if (metadata is null) return new(Error: "not_found");
            if (a.Revision != input.ExpectedAccountRevision || metadata.Revision != input.ExpectedProtectionRevision
                || a.Revision >= ProtocolBinary.MaxInteger || metadata.Revision >= ProtocolBinary.MaxInteger
                || metadata.KeyEpoch >= ProtocolBinary.MaxInteger || a.RevocationGeneration >= ProtocolBinary.MaxInteger)
                return new(Error: "revision_conflict");
            if (a.PolicyRevision <= 0 || a.RevocationGeneration <= 0) return new(Error: "persistence_unavailable");

            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE account_id={a.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE", cancellationToken);
            var session = await db.VaultAccessSessions.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == a.Id && x.HandleVerifier == request.AccessVerifier, cancellationToken);
            var now = await Now(db, cancellationToken);
            bool Permitted(DateTimeOffset at) => session is not null && session.ProfileId is null && !session.Revoked
                && session.PolicyRevision == a.PolicyRevision && session.RevocationGeneration == a.RevocationGeneration
                && session.IssuedAt <= at && (a.RenewalEnabled ? session.ExpiresAt > at : session.ExpiresAt is null);
            if (!Permitted(now)) return new(Error: "vault_access_denied");

            var protection = await db.VaultProtections.SingleAsync(x => x.Id == metadata.Id, cancellationToken);
            var current = JsonSerializer.Deserialize<ProtectionMaterial>(protection.Material, Json);
            if (current?.IsValid() != true || current.PasswordWrapper.KeyEpoch != metadata.KeyEpoch
                || current.RecoveryWrapper.Generation != metadata.RecoveryGeneration) return new(Error: "persistence_unavailable");
            if (!input.IsValidTransition(current)) return new(Error: "validation_failed");
            byte[]? replacement = null;
            var profileWrites = new List<(Profile Row, ContentReplacement Replacement)>();
            if (input.Mode == "rotate-content")
            {
                var existing = await db.Accounts.Where(x => x.Id == a.Id).Select(x => x.DetailsEnvelope).SingleAsync(cancellationToken);
                var content = JsonSerializer.Deserialize<EncryptedEnvelope>(existing, Json);
                if (content?.IsValid() != true) return new(Error: "persistence_unavailable");
                if (content.KeyEpoch >= ProtocolBinary.MaxInteger) return new(Error: "revision_conflict");
                var accountReplacement = input.ContentReplacements.Single(x => x.ResourceKind == "account");
                if (accountReplacement.Envelope.KeyEpoch != content.KeyEpoch + 1) return new(Error: "validation_failed");
                replacement = JsonSerializer.SerializeToUtf8Bytes(accountReplacement.Envelope, Json);
                // Include trash: retained ciphertext must not escape a complete rotation.
                var profiles = await db.Profiles.Where(x => x.AccountId == a.Id).ToListAsync(cancellationToken);
                if (input.ContentReplacements.Length != profiles.Count + 1) return new(Error: "validation_failed");
                var replacements = input.ContentReplacements.Where(x => x.ResourceKind == "profile").ToDictionary(x => x.ResourceId);
                foreach (var profile in profiles)
                {
                    if (!replacements.TryGetValue(profile.PublicId, out var item)) return new(Error: "validation_failed");
                    if (profile.Revision != item.ExpectedRevision || profile.Revision >= ProtocolBinary.MaxInteger) return new(Error: "revision_conflict");
                    var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(profile.Envelope, Json);
                    var wrappers = JsonSerializer.Deserialize<ProfileKeyWrappers>(profile.KeyWrappers, Json);
                    if (envelope?.IsValid() != true || wrappers?.IsBound(a.PublicId,profile.PublicId,envelope.KeyEpoch,profile.Revision,request.Actor,current) != true)
                        return new(Error: "persistence_unavailable");
                    if (envelope.KeyEpoch >= ProtocolBinary.MaxInteger) return new(Error: "revision_conflict");
                    if (item.Envelope.KeyEpoch != envelope.KeyEpoch + 1 || item.Envelope.KeySalt == envelope.KeySalt || item.Envelope.Nonce == envelope.Nonce
                        || item.KeyWrappers?.IsValidRotation(wrappers) != true
                        || !item.KeyWrappers.IsBound(a.PublicId,profile.PublicId,item.Envelope.KeyEpoch,profile.Revision+1,request.Actor,current))
                        return new(Error: "validation_failed");
                    profileWrites.Add((profile,item));
                }
            }
            var row = await db.VaultUnlockChallenges.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == a.Id && x.PublicId == request.ChallengeId, cancellationToken);
            if (row is null || row.Consumed) return new(Error: "vault_proof_rejected");
            var challenge = JsonSerializer.Deserialize<VaultProofChallenge>(row.Challenge, Json) ?? throw new JsonException();
            if (challenge.ChallengeId != row.PublicId || row.ExpiresAt != DateTimeOffset.FromUnixTimeSeconds(challenge.ExpiresAt)) return new(Error: "persistence_unavailable");
            if (row.PolicyRevision != a.PolicyRevision || row.RevocationGeneration != a.RevocationGeneration
                || challenge.ProtectionRevision != metadata.Revision || challenge.KeyEpoch != metadata.KeyEpoch) return new(Error: "revision_conflict");
            now = await Now(db, cancellationToken);
            if (!Permitted(now)) return new(Error: "vault_access_denied");
            var binding = new VaultProofBinding("change-protection", request.Actor, a.PublicId, "account", a.PublicId, metadata.KeyEpoch, metadata.Revision, null);
            if (!VaultProof.Verify(challenge, binding, request.RawBody, current.UnlockVerifier, request.Proof, now.ToUnixTimeSeconds())
                || !MatchesSignedBody(input, request.RawBody)) return new(Error: "vault_proof_rejected");

            // This is the permission/proof linearization point, using current statement
            // time after both locks rather than transaction-start time.
            var consumed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cerberus.vault_unlock_challenge SET consumed=TRUE
                WHERE id={row.Id} AND NOT consumed AND expires_at>statement_timestamp()
                AND EXISTS(SELECT 1 FROM cerberus.vault_access_session
                    WHERE account_id={a.Id} AND handle_verifier={request.AccessVerifier}
                    AND profile_id IS NULL AND NOT revoked AND policy_revision={a.PolicyRevision}
                    AND revocation_generation={a.RevocationGeneration} AND issued_at<=statement_timestamp()
                    AND (({a.RenewalEnabled} AND expires_at>statement_timestamp())
                        OR (NOT {a.RenewalEnabled} AND expires_at IS NULL)))
                """, cancellationToken);
            if (consumed != 1) return new(Error: Permitted(await Now(db, cancellationToken)) ? "vault_proof_rejected" : "vault_access_denied");
            protection.Material = JsonSerializer.SerializeToUtf8Bytes(input.Material, Json);
            protection.Revision++;
            protection.KeyEpoch = input.Material.PasswordWrapper.KeyEpoch;
            var account = db.Accounts.Where(x => x.Id == a.Id && x.Revision == input.ExpectedAccountRevision);
            var changed = replacement is null
                ? await account.ExecuteUpdateAsync(set => set.SetProperty(x => x.RevocationGeneration, x => x.RevocationGeneration + 1), cancellationToken)
                : await account.ExecuteUpdateAsync(set => set.SetProperty(x => x.RevocationGeneration, x => x.RevocationGeneration + 1)
                    .SetProperty(x => x.DetailsEnvelope, replacement).SetProperty(x => x.Revision, x => x.Revision + 1), cancellationToken);
            if (changed != 1) return new(Error: "revision_conflict");
            foreach (var (profile,item) in profileWrites)
            {
                profile.Envelope = JsonSerializer.SerializeToUtf8Bytes(item.Envelope,Json);
                profile.KeyWrappers = JsonSerializer.SerializeToUtf8Bytes(item.KeyWrappers,Json);
                profile.Revision++;
                profile.ServerSequence = await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync(cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(new(a.PublicId, protection.Revision, protection.KeyEpoch, protection.RecoveryGeneration, a.Revision + (replacement is null ? 0 : 1)));
        }
        catch (PostgresException exception) when (exception.SqlState == "2200H") { return new(Error: "revision_conflict"); }
        catch (DbUpdateConcurrencyException) { return new(Error: "revision_conflict"); }
        catch (Exception exception) when (exception is DbException or DbUpdateException or TimeoutException or JsonException or ArgumentOutOfRangeException
            || exception is InvalidOperationException { InnerException: DbUpdateException or DbException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
    }
    private static bool MatchesSignedBody(ProtectionChange input, byte[] raw)
    {
        try
        {
            var signed = JsonSerializer.Deserialize<ProtectionChange>(raw, Json);
            return signed is not null && signed.AccountId == input.AccountId
                && signed.ExpectedProtectionRevision == input.ExpectedProtectionRevision
                && signed.ExpectedAccountRevision == input.ExpectedAccountRevision && signed.Mode == input.Mode
                && signed.Material == input.Material && signed.ContentReplacements is not null
                && signed.ContentReplacements.SequenceEqual(input.ContentReplacements);
        }
        catch (JsonException) { return false; }
    }
    private static Task<DateTimeOffset> Now(AppDbContext db, CancellationToken ct) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
}
