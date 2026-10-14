using System.Data.Common;
using Npgsql;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Accounts;

public sealed class AccountClosureStore(IDbContextFactory<AppDbContext> factory) : IAccountClosureStore
{
    public async Task<AccountClosureResult> RequestAsync(AccountClosureRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (!new AccountClosureInput(request.ExpectedRevision).IsValid()) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var initial = await Actor(db, request.Actor, ct);
            if (initial is null || await Erased(db, initial, ct)) return new(Error: "not_found");
            var ids = (await Connections(db, initial.Id, ct)).Append(initial.Id).Distinct().ToArray();
            var roots = await db.Accounts.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.PublicId).ToArrayAsync(ct);
            if (roots.Length != ids.Length || roots.Any(x => x == Guid.Empty)) return new(Error: "persistence_unavailable");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE public_id=ANY({roots}) ORDER BY public_id FOR UPDATE", ct);
            var owner = await Actor(db, request.Actor, ct); var now = await Now(db, ct);
            if (owner is null || owner.Id != initial.Id || await Erased(db, owner, ct)) return new(Error: "not_found");
            if (!FreshAuthentication.IsFresh(request.AuthenticationIssuedAt, now)) return new(Error: "fresh_authentication_required");
            if (!Valid(owner)) return new(Error: "persistence_unavailable");
            if (owner.State == AccountState.ClosurePending)
                return await Pending(db, owner, now, ct);
            if (!(await Connections(db, owner.Id, ct)).Append(owner.Id).ToHashSet().SetEquals(ids)) return new(Error: "revision_conflict");
            if (owner.Revision != request.ExpectedRevision || owner.Revision == ProtocolBinary.MaxInteger || owner.RevocationGeneration == ProtocolBinary.MaxInteger)
                return new(Error: "revision_conflict");
            // Native shared writers acquire collection locks before target locks.
            // Hold affected collections before reducing grants or waiting on child rows.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT FROM cerberus.collection c WHERE c.account_id={owner.Id}
                  OR EXISTS(SELECT FROM cerberus.collection_grant g WHERE g.collection_id=c.id AND g.recipient_account_id={owner.Id})
                ORDER BY c.public_id FOR NO KEY UPDATE OF c
                """, ct);
            now = await Now(db, ct);
            if (!FreshAuthentication.IsFresh(request.AuthenticationIssuedAt, now)) return new(Error: "fresh_authentication_required");
            var grants = await (from g in db.CollectionGrants.AsNoTracking() join c in db.Collections on g.CollectionId equals c.Id
                                where c.AccountId == owner.Id || g.RecipientAccountId == owner.Id
                                select new Grant(g.Id, g.PublicId, g.CollectionId, c.AccountId, g.RecipientAccountId, g.Access, g.State, g.Revision)).ToListAsync(ct);
            var software = await db.SoftwareGrants.AsNoTracking().Where(x => x.AccountId == owner.Id).ToListAsync(ct);
            if (grants.Any(x => x.PublicId == Guid.Empty || !Safe(x.Revision) || x.State is not (CollectionGrantState.Active or CollectionGrantState.Revoked))
                || software.Any(x => x.PublicId == Guid.Empty || !Safe(x.Revision) || x.State is not (SoftwareGrantState.Active or SoftwareGrantState.Revoked)))
                return new(Error: "persistence_unavailable");
            if (grants.Any(x => x.State == CollectionGrantState.Active && x.Revision == ProtocolBinary.MaxInteger)
                || software.Any(x => x.State == SoftwareGrantState.Active && x.Revision == ProtocolBinary.MaxInteger)) return new(Error: "revision_conflict");
            var recipients = grants.Where(x => x.OwnerId == owner.Id && x.RecipientId != owner.Id && x.State == CollectionGrantState.Active)
                .Select(x => x.RecipientId).Distinct().ToArray();
            var generations = await db.Accounts.AsNoTracking().Where(x => recipients.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.RevocationGeneration, ct);
            if (generations.Count != recipients.Length || generations.Values.Any(x => !Safe(x))) return new(Error: "persistence_unavailable");
            if (generations.Values.Any(x => x == ProtocolBinary.MaxInteger)) return new(Error: "revision_conflict");
            var key = "account-purge/" + owner.PublicId;
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.retention_work_item WHERE operation_key={key} FOR UPDATE", ct);
            var work = await db.RetentionWorkItems.SingleOrDefaultAsync(x => x.OperationKey == key, ct);
            if (work is { CompletedAt: null }) return new(Error: "persistence_unavailable");
            var collateral = await Collateral(db, owner.Id, ids, recipients, ct);
            var stamp = Guid.NewGuid();
            // This database-time CAS authorizes the full reduction. Natural token
            // expiry after this point does not undo an already authorized close.
            var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cerberus.account SET state={AccountState.ClosurePending},revision=revision+1,
                  revocation_generation=revocation_generation+1,closure_requested_at=statement_timestamp(),
                  closure_purge_at=statement_timestamp()+interval '720 hours',concurrency_stamp={stamp}
                WHERE id={owner.Id} AND heimdall_public_id={request.Actor} AND public_id={owner.PublicId}
                  AND state={AccountState.Active} AND revision={owner.Revision} AND revocation_generation={owner.RevocationGeneration}
                  AND closure_requested_at IS NULL AND closure_purge_at IS NULL
                  AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id={owner.PublicId})
                  AND {request.AuthenticationIssuedAt}>0
                  AND {request.AuthenticationIssuedAt}<=floor(extract(epoch FROM statement_timestamp()))::bigint
                  AND floor(extract(epoch FROM statement_timestamp()))::bigint-{request.AuthenticationIssuedAt}<60
                """, ct);
            if (changed != 1) return new(Error: !FreshAuthentication.IsFresh(request.AuthenticationIssuedAt, await Now(db, ct))
                ? "fresh_authentication_required" : "persistence_unavailable");
            var saved = await Actor(db, request.Actor, ct);
            if (saved is null || saved.State != AccountState.ClosurePending || saved.Revision != owner.Revision + 1
                || saved.RevocationGeneration != owner.RevocationGeneration + 1 || saved.ConcurrencyStamp != stamp || !Valid(saved) || !Unchanged(owner, saved))
                return new(Error: "persistence_unavailable");
            var sessionCount = await db.VaultAccessSessions.CountAsync(x => x.AccountId == owner.Id, ct);
            if (await db.VaultAccessSessions.Where(x => x.AccountId == owner.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.Revoked, true), ct) != sessionCount)
                return new(Error: "persistence_unavailable");
            var challengeCount = await db.VaultUnlockChallenges.CountAsync(x => x.AccountId == owner.Id, ct);
            if (await db.VaultUnlockChallenges.Where(x => x.AccountId == owner.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.Consumed, true), ct) != challengeCount)
                return new(Error: "persistence_unavailable");
            foreach (var grant in grants.OrderBy(x => x.PublicId))
            {
                var revision = grant.Revision + (grant.State == CollectionGrantState.Active ? 1 : 0);
                if (await db.CollectionGrants.Where(x => x.Id == grant.Id && x.PublicId == grant.PublicId && x.CollectionId == grant.CollectionId
                        && x.RecipientAccountId == grant.RecipientId && x.State == grant.State && x.Revision == grant.Revision)
                    .ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked).SetProperty(g => g.Revision, revision)
                        .SetProperty(g => g.RecipientKeyEnvelope, Array.Empty<byte>()).SetProperty(g => g.ConcurrencyStamp, stamp), ct) != 1)
                    return new(Error: "persistence_unavailable");
            }
            foreach (var recipient in generations)
                if (await db.Accounts.Where(x => x.Id == recipient.Key && x.RevocationGeneration == recipient.Value)
                    .ExecuteUpdateAsync(x => x.SetProperty(a => a.RevocationGeneration, recipient.Value + 1).SetProperty(a => a.ConcurrencyStamp, stamp), ct) != 1)
                    return new(Error: "persistence_unavailable");
            foreach (var grant in software)
                if (await db.SoftwareGrants.Where(x => x.Id == grant.Id && x.AccountId == owner.Id && x.State == grant.State && x.Revision == grant.Revision)
                    .ExecuteUpdateAsync(x => x.SetProperty(g => g.State, SoftwareGrantState.Revoked)
                        .SetProperty(g => g.Revision, grant.Revision + (grant.State == SoftwareGrantState.Active ? 1 : 0))
                        .SetProperty(g => g.RecipientKey, Array.Empty<byte>()).SetProperty(g => g.ConcurrencyStamp, stamp), ct) != 1)
                    return new(Error: "persistence_unavailable");
            var recordTargets = await db.SoftwareGrantRecords.CountAsync(x => x.AccountId == owner.Id, ct);
            var collectionTargets = await db.SoftwareGrantCollections.CountAsync(x => x.AccountId == owner.Id, ct);
            if (await db.SoftwareGrantRecords.Where(x => x.AccountId == owner.Id).ExecuteDeleteAsync(ct) != recordTargets
                || await db.SoftwareGrantCollections.Where(x => x.AccountId == owner.Id).ExecuteDeleteAsync(ct) != collectionTargets)
                return new(Error: "persistence_unavailable");
            if (work is null) { work = new RetentionWorkItem { OperationKey = key }; db.RetentionWorkItems.Add(work); }
            work.DueAt = saved.ClosurePurgeAt!.Value; work.CompletedAt = null; work.ClaimToken = null; work.ClaimExpiresAt = null; work.Attempts = 0;
            await db.SaveChangesAsync(ct);
            var finalOwner = await Actor(db, request.Actor, ct);
            if (finalOwner is null || !Valid(finalOwner) || !Unchanged(owner, finalOwner)
                || finalOwner.State != AccountState.ClosurePending || finalOwner.Revision != saved.Revision
                || finalOwner.RevocationGeneration != saved.RevocationGeneration || finalOwner.ConcurrencyStamp != stamp
                || finalOwner.ClosureRequestedAt != saved.ClosureRequestedAt || finalOwner.ClosurePurgeAt != saved.ClosurePurgeAt
                || await Erased(db, finalOwner, ct) || await Collateral(db, owner.Id, ids, recipients, ct) != collateral
                || await db.VaultAccessSessions.AnyAsync(x => x.AccountId == owner.Id && !x.Revoked, ct)
                || await db.VaultUnlockChallenges.AnyAsync(x => x.AccountId == owner.Id && !x.Consumed, ct)
                || await db.SoftwareGrantRecords.AnyAsync(x => x.AccountId == owner.Id, ct)
                || await db.SoftwareGrantCollections.AnyAsync(x => x.AccountId == owner.Id, ct))
                return new(Error: "persistence_unavailable");
            var finalGrants = await (from g in db.CollectionGrants.AsNoTracking() join c in db.Collections on g.CollectionId equals c.Id
                                     where c.AccountId == owner.Id || g.RecipientAccountId == owner.Id select g).ToListAsync(ct);
            if (finalGrants.Count != grants.Count) return new(Error: "persistence_unavailable");
            foreach (var grant in grants)
            {
                var post = finalGrants.SingleOrDefault(x => x.Id == grant.Id);
                if (post is null || post.PublicId != grant.PublicId || post.CollectionId != grant.CollectionId
                    || post.RecipientAccountId != grant.RecipientId || post.Access != grant.Access
                    || post.State != CollectionGrantState.Revoked || post.Revision != grant.Revision + (grant.State == CollectionGrantState.Active ? 1 : 0)
                    || post.ConcurrencyStamp != stamp || post.RecipientKeyEnvelope.Length != 0) return new(Error: "persistence_unavailable");
            }
            var finalSoftware = await db.SoftwareGrants.AsNoTracking().Where(x => x.AccountId == owner.Id).ToListAsync(ct);
            if (finalSoftware.Count != software.Count) return new(Error: "persistence_unavailable");
            foreach (var grant in software)
            {
                var post = finalSoftware.SingleOrDefault(x => x.Id == grant.Id);
                if (post is null || post.PublicId != grant.PublicId || post.AccountId != grant.AccountId
                    || post.HeimdallSoftwarePublicId != grant.HeimdallSoftwarePublicId || post.State != SoftwareGrantState.Revoked
                    || post.Revision != grant.Revision + (grant.State == SoftwareGrantState.Active ? 1 : 0)
                    || post.ConcurrencyStamp != stamp || post.RecipientKey.Length != 0) return new(Error: "persistence_unavailable");
            }
            foreach (var recipient in generations)
                if (!await db.Accounts.AnyAsync(x => x.Id == recipient.Key && x.RevocationGeneration == recipient.Value + 1, ct)) return new(Error: "persistence_unavailable");
            var finalWork = await db.RetentionWorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.OperationKey == key, ct);
            if (finalWork is null || finalWork.Id != work.Id || finalWork.PublicId != work.PublicId
                || finalWork.PublicId == Guid.Empty || finalWork.Attempts != 0 || finalWork.ClaimToken is not null
                || finalWork.ClaimExpiresAt is not null || finalWork.CompletedAt is not null || finalWork.DueAt != saved.ClosurePurgeAt)
                return new(Error: "persistence_unavailable");
            var result = await Pending(db, finalOwner, await Now(db, ct), ct, replay: false);
            if (result.Error is not null) return result;
            await tx.CommitAsync(ct); return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (DbUpdateConcurrencyException) { return new(Error: "revision_conflict"); }
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }
    private static Task<string> Collateral(AppDbContext db, long owner, long[] roots, long[] recipients, CancellationToken ct) =>
        db.Database.SqlQueryRaw<string>("""
            WITH retained(kind,public_id) AS (
              SELECT 'account',public_id FROM cerberus.account WHERE id=ANY(@roots)
              UNION SELECT 'profile',public_id FROM cerberus.profile WHERE account_id=@owner
              UNION SELECT 'record',public_id FROM cerberus.record WHERE account_id=@owner
              UNION SELECT 'folder',public_id FROM cerberus.folder WHERE account_id=@owner
              UNION SELECT 'collection',c.public_id FROM cerberus.collection c WHERE c.account_id=@owner
                OR EXISTS(SELECT FROM cerberus.collection_grant g WHERE g.collection_id=c.id AND g.recipient_account_id=@owner)
              UNION SELECT 'grant',g.public_id FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id
                WHERE c.account_id=@owner OR EXISTS(SELECT FROM cerberus.collection_grant incoming WHERE incoming.collection_id=c.id AND incoming.recipient_account_id=@owner)
              UNION SELECT 'grant',public_id FROM cerberus.software_grant WHERE account_id=@owner)
            SELECT jsonb_build_object(
            'terminal',(SELECT jsonb_agg(to_jsonb(e) ORDER BY resource_kind,resource_id) FROM cerberus.terminal_erasure e
              WHERE EXISTS(SELECT FROM retained r WHERE r.kind=e.resource_kind AND r.public_id=e.resource_id)),
            'accounts',(SELECT jsonb_agg(CASE WHEN r.id=@owner THEN to_jsonb(r)-ARRAY['state','revision','revocation_generation','closure_requested_at','closure_purge_at','concurrency_stamp']
              WHEN r.id=ANY(@recipients) THEN to_jsonb(r)-ARRAY['revocation_generation','concurrency_stamp'] ELSE to_jsonb(r) END ORDER BY r.id)
              FROM cerberus.account r WHERE r.id=ANY(@roots)),
            'incoming_collections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c.id) FROM cerberus.collection c
              WHERE c.account_id<>@owner AND EXISTS(SELECT FROM cerberus.collection_grant g WHERE g.collection_id=c.id AND g.recipient_account_id=@owner)),
            'incoming_siblings',(SELECT jsonb_agg(to_jsonb(g) ORDER BY g.id) FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id
              WHERE c.account_id<>@owner AND g.recipient_account_id<>@owner
                AND EXISTS(SELECT FROM cerberus.collection_grant incoming WHERE incoming.collection_id=c.id AND incoming.recipient_account_id=@owner)),
            'native_grants',(SELECT jsonb_agg(to_jsonb(g)-ARRAY['state','revision','recipient_key_envelope','concurrency_stamp'] ORDER BY g.id)
              FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id WHERE c.account_id=@owner OR g.recipient_account_id=@owner),
            'software_grants',(SELECT jsonb_agg(to_jsonb(g)-ARRAY['state','revision','recipient_key','concurrency_stamp'] ORDER BY g.id)
              FROM cerberus.software_grant g WHERE g.account_id=@owner),
            'vault_protection',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.vault_protection r WHERE r.account_id=@owner),
            'vault_recovery_operation',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.vault_recovery_operation r WHERE r.account_id=@owner),
            'profile',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.profile r WHERE r.account_id=@owner),
            'record',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.record r WHERE r.account_id=@owner),
            'folder',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.folder r WHERE r.account_id=@owner),
            'collection',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.collection r WHERE r.account_id=@owner),
            'profile_record',(SELECT jsonb_agg(to_jsonb(r) ORDER BY profile_id,record_id) FROM cerberus.profile_record r WHERE r.account_id=@owner),
            'profile_folder',(SELECT jsonb_agg(to_jsonb(r) ORDER BY profile_id,folder_id) FROM cerberus.profile_folder r WHERE r.account_id=@owner),
            'collection_record',(SELECT jsonb_agg(to_jsonb(r) ORDER BY collection_id,record_id) FROM cerberus.collection_record r WHERE r.account_id=@owner),
            'collection_folder',(SELECT jsonb_agg(to_jsonb(r) ORDER BY collection_id,folder_id) FROM cerberus.collection_folder r WHERE r.account_id=@owner),
            'trash_operation',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.trash_operation r WHERE r.account_id=@owner),
            'sync_stream',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.sync_stream r WHERE r.account_id=@owner),
            'sync_inventory',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.sync_inventory r JOIN cerberus.sync_stream s ON s.id=r.stream_id WHERE s.account_id=@owner),
            'sync_change',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.sync_change r JOIN cerberus.sync_stream s ON s.id=r.stream_id WHERE s.account_id=@owner),
            'sync_operation',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.sync_operation r WHERE r.account_id=@owner),
            'profile_collection',(SELECT jsonb_agg(to_jsonb(r) ORDER BY profile_id,collection_id) FROM cerberus.profile_collection r JOIN cerberus.profile p ON p.id=r.profile_id WHERE p.account_id=@owner),
            'trash_entry',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.trash_entry r JOIN cerberus.trash_operation t ON t.id=r.operation_id WHERE t.account_id=@owner),
            'sessions',(SELECT jsonb_agg(to_jsonb(r)-'revoked' ORDER BY id) FROM cerberus.vault_access_session r WHERE r.account_id=@owner),
            'challenges',(SELECT jsonb_agg(to_jsonb(r)-'consumed' ORDER BY id) FROM cerberus.vault_unlock_challenge r WHERE r.account_id=@owner))::text AS "Value"
            """, new NpgsqlParameter("owner", owner), new NpgsqlParameter("roots", roots), new NpgsqlParameter("recipients", recipients)).SingleAsync(ct);
    private static Task<Account?> Actor(AppDbContext db, Guid actor, CancellationToken ct) => db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.HeimdallPublicId == actor, ct);
    private static Task<bool> Erased(AppDbContext db, Account owner, CancellationToken ct) => db.TerminalErasures.AnyAsync(x => x.ResourceKind == "account" && x.ResourceId == owner.PublicId, ct);
    private static Task<DateTimeOffset> Now(AppDbContext db, CancellationToken ct) => db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
    private static Task<long[]> Connections(AppDbContext db, long owner, CancellationToken ct) =>
        (from g in db.CollectionGrants join c in db.Collections on g.CollectionId equals c.Id
         where c.AccountId == owner || g.RecipientAccountId == owner
         select c.AccountId == owner ? g.RecipientAccountId : c.AccountId).Distinct().ToArrayAsync(ct);
    private static bool Safe(long value) => value is > 0 and <= ProtocolBinary.MaxInteger;
    private static bool Valid(Account a) => a.PublicId != Guid.Empty && Safe(a.Revision) && Safe(a.RevocationGeneration) && Safe(a.PolicyRevision)
        && (a.State == AccountState.Active ? a.ClosureRequestedAt is null && a.ClosurePurgeAt is null
            : a.State == AccountState.ClosurePending && a.ClosureRequestedAt is { } start && a.ClosurePurgeAt is { } end
                && start.Offset == TimeSpan.Zero && start.Ticks >= 10 && start.Ticks % 10 == 0 && end.Offset == TimeSpan.Zero
                && end.Ticks % 10 == 0 && end - start == TimeSpan.FromDays(30));
    private static bool Unchanged(Account a, Account b) => a.Id == b.Id && a.PublicId == b.PublicId && a.HeimdallPublicId == b.HeimdallPublicId
        && a.PolicyRevision == b.PolicyRevision && a.RenewalEnabled == b.RenewalEnabled && a.RenewalInterval == b.RenewalInterval
        && a.DetailsEnvelope.AsSpan().SequenceEqual(b.DetailsEnvelope);
    private static async Task<AccountClosureResult> Pending(AppDbContext db, Account a, DateTimeOffset now, CancellationToken ct, bool replay = true)
    {
        if (!Valid(a) || a.State != AccountState.ClosurePending) return new(Error: "persistence_unavailable");
        var work = await db.RetentionWorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.OperationKey == "account-purge/" + a.PublicId, ct);
        if (work is null || work.DueAt != a.ClosurePurgeAt || work.CompletedAt is not null) return new(Error: "persistence_unavailable");
        var details = new AccountClosureDetails(a.PublicId, a.Revision, a.RevocationGeneration, a.ClosureRequestedAt!.Value, a.ClosurePurgeAt!.Value, now, replay);
        return details.IsValid() ? new(details) : new(Error: "persistence_unavailable");
    }
    private sealed record Grant(long Id, Guid PublicId, long CollectionId, long OwnerId, long RecipientId, CollectionGrantAccess Access, CollectionGrantState State, long Revision);
}
