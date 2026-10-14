using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Shared.Configuration;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Sync;

public sealed class SyncEditsStore(IDbContextFactory<AppDbContext> factory, CerberusOptions options) : ISyncEditsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false };

    public async Task<SyncEditsResult> UploadAsync(SyncEditsRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (string.IsNullOrWhiteSpace(request.AccessVerifier)) return new(Error: "vault_access_required");
        if (request.Input?.IsValid(options.MaxPageSize) != true) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var initial = await ReadScope(db, request, ct);
            var error = AuthorityError(initial); if (error is not null) return new(Error: error);
            var original = initial!;
            // All roots in the same public-ID order as sharing and download. The
            // Exclusive owner roots also serialize batches from distinct recipients:
            // native target locks survive savepoint release and client item order
            // must not create opposing multi-resource lock acquisition.
            var accounts = original.ForeignOwners.Append(original.AccountId).Distinct().OrderBy(x => x).ToArray();
            foreach (var id in accounts)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE public_id={id} FOR UPDATE", ct);
            }
            error = Recheck(await ReadScope(db, request, ct), original); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE id={original.SessionId} AND account_id={original.InternalId} FOR UPDATE", ct);
            if (original.ProfileInternalId is { } profile)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE id={profile} AND account_id={original.InternalId} FOR UPDATE", ct);
            error = Recheck(await ReadScope(db, request, ct), original); if (error is not null) return new(Error: error);
            // Standalone shared record/folder writers take collection SHARE locks
            // before target locks, without locking the foreign owner's account.
            // Lock all captured owners' collections before any child target lock:
            // later collection edits must not upgrade a lock behind such a writer.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT FROM cerberus.collection c JOIN cerberus.account owner ON owner.id=c.account_id
                WHERE owner.public_id=ANY({accounts})
                ORDER BY c.public_id FOR NO KEY UPDATE OF c
                """, ct);
            error = Recheck(await ReadScope(db, request, ct), original); if (error is not null) return new(Error: error);
            var contexts = new TransactionContextFactory(factory, db);
            var outcomes = new List<SyncEditOutcome>();
            foreach (var edit in request.Input.Edits)
            {
                var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { original.ProfileId, Edit = edit }, Json));
                var existing = await db.SyncOperations.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == original.InternalId && x.OperationId == edit.OperationId, ct);
                if (existing is not null)
                {
                    if (!StoredValid(existing)) return new(Error: "persistence_unavailable");
                    if (existing.ScopeProfileId != original.ProfileId || !CryptographicOperations.FixedTimeEquals(existing.RequestFingerprint, fingerprint))
                        return new(Error: "operation_conflict");
                    if (existing.ResourceKind != edit.ResourceKind || existing.ResourceId != edit.ResourceId || existing.Action != edit.Action)
                        return new(Error: "persistence_unavailable");
                }
                var terminal = await db.TerminalErasures.AnyAsync(x => x.ResourceKind == edit.ResourceKind && x.ResourceId == edit.ResourceId, ct);
                SyncEditOutcome outcome;
                if (terminal || existing?.Status == "permanently-deleted")
                {
                    var known = existing?.Status is "applied" or "superseded" or "permanently-deleted" || await Known(db, original, edit, ct);
                    outcome = new(edit.OperationId, edit.ResourceKind, edit.ResourceId, known ? "permanently-deleted" : "denied");
                }
                else if (existing is not null)
                {
                    if (existing.Status == "denied") outcome = new(edit.OperationId, edit.ResourceKind, edit.ResourceId, "denied");
                    else
                    {
                        // Recheck native WRITE authority without executing another
                        // write; current grant/ancestry/protection validation is reused.
                        var permitted = await Inspect(contexts, request, edit, ct);
                        if (permitted.Error is "not_found" or "vault_access_denied") outcome = new(edit.OperationId, edit.ResourceKind, edit.ResourceId, "denied");
                        else if (permitted.Error is not null) return new(Error: permitted.Error);
                        else outcome = Outcome(existing);
                    }
                }
                else
                {
                    var result = await Apply(contexts, request, edit, ct);
                    if (result.Error is null or "superseded")
                    {
                        var data = result.Data;
                        if (data is null || data.Id != edit.ResourceId) return new(Error: "persistence_unavailable");
                        outcome = new(edit.OperationId, edit.ResourceKind, edit.ResourceId, result.Error == "superseded" ? "superseded" : "applied",
                            data.Revision, data.Sequence, data.EditedAt);
                    }
                    else if (result.Error is "not_found" or "vault_access_denied")
                        outcome = new(edit.OperationId, edit.ResourceKind, edit.ResourceId, "denied");
                    else if (result.Error == "revision_conflict" && edit.Action == "create")
                    {
                        var visible = await Inspect(contexts, request, edit, ct);
                        if (visible.Error is "not_found" or "vault_access_denied") outcome = new(edit.OperationId, edit.ResourceKind, edit.ResourceId, "denied");
                        else if (visible.Error is not null) return new(Error: visible.Error);
                        else outcome = new(edit.OperationId, edit.ResourceKind, edit.ResourceId, "superseded", visible.Data!.Revision, visible.Data.Sequence, visible.Data.EditedAt);
                    }
                    else return new(Error: result.Error);
                }
                if (!outcome.IsValid()) return new(Error: "persistence_unavailable");
                error = Recheck(await ReadScope(db, request, ct), original); if (error is not null) return new(Error: error);
                if (existing is null)
                {
                    var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
                    db.SyncOperations.Add(new() { AccountId = original.InternalId, OperationId = edit.OperationId, ScopeProfileId = original.ProfileId,
                        RequestFingerprint = fingerprint, ResourceKind = edit.ResourceKind, ResourceId = edit.ResourceId, Action = edit.Action,
                        Status = outcome.Status, Revision = outcome.Revision, ServerSequence = outcome.ServerSequence, EditedAt = outcome.EditedAt, CreatedAt = now });
                    await db.SaveChangesAsync(ct);
                }
                outcomes.Add(outcome);
            }
            var final = await ReadScope(db, request, ct);
            error = Recheck(final, original); if (error is not null) return new(Error: error);
            await tx.CommitAsync(ct);
            return new(new(final!.AccountId, final.ProfileId, final.DatabaseNow, outcomes));
        }
        catch (Exception e) when (e is PostgresException { SqlState: "2200H" }
            or DbUpdateException { InnerException: PostgresException { SqlState: "2200H" } }) { return new(Error: "revision_conflict"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (DbUpdateConcurrencyException) { return new(Error: "revision_conflict"); }
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }
    private static bool StoredValid(SyncOperation value) => value.RequestFingerprint.Length == 32 && value.Action is "create" or "update"
        && value.ScopeProfileId != Guid.Empty && SyncResourceData.ValidTime(value.CreatedAt) && Outcome(value).IsValid();
    private static SyncEditOutcome Outcome(SyncOperation value) => new(value.OperationId, value.ResourceKind, value.ResourceId, value.Status, value.Revision, value.ServerSequence, value.EditedAt);
    private static async Task<bool> Known(AppDbContext db, Scope scope, SyncEditInput edit, CancellationToken ct) =>
        await db.SyncInventories.AnyAsync(x => x.ResourceKind == edit.ResourceKind && x.ResourceId == edit.ResourceId
            && db.SyncStreams.Any(s => s.Id == x.StreamId && s.AccountId == scope.InternalId && (scope.ProfileId == null || s.ProfileId == scope.ProfileInternalId)), ct)
        || await db.SyncOperations.AnyAsync(x => x.AccountId == scope.InternalId && x.ScopeProfileId == scope.ProfileId
            && x.ResourceKind == edit.ResourceKind && x.ResourceId == edit.ResourceId && (x.Status == "applied" || x.Status == "superseded"), ct);

    private sealed record Metadata(Guid Id, long Revision, long Sequence, DateTimeOffset EditedAt);
    private sealed record NativeResult(Metadata? Data = null, string? Error = null);
    private static NativeResult Map<T>(VaultResult<T> result, Func<T, Metadata> metadata) where T : class => new(result.Data is null ? null : metadata(result.Data), result.Error);
    private static async Task<NativeResult> Apply(IDbContextFactory<AppDbContext> contexts, SyncEditsRequest r, SyncEditInput e, CancellationToken ct)
    {
        return e switch
        {
            SyncRecordCreate c => Map(await new RecordCreateStore(contexts).CreateAsync(new(r.Actor, r.AccessVerifier, c.Input), ct), x => new(x.RecordId, x.Revision, x.ServerSequence, x.EditedAt)),
            SyncFolderCreate c => Map(await new FolderCreateStore(contexts).CreateAsync(new(r.Actor, r.AccessVerifier, c.Input), ct), x => new(x.FolderId, x.Revision, x.ServerSequence, x.EditedAt)),
            SyncCollectionCreate c => Map(await new CollectionCreateStore(contexts).CreateAsync(new(r.Actor, r.AccessVerifier, c.Input), ct), x => new(x.CollectionId, x.Revision, x.ServerSequence, x.EditedAt)),
            SyncProfileCreate c => Map(await new ProfileCreateStore(contexts).CreateAsync(new(r.Actor, r.AccessVerifier, c.Input), ct), x => new(x.ProfileId, x.Revision, x.ServerSequence, x.EditedAt)),
            SyncContentUpdate u => await Update(contexts, r, e.ResourceKind, u.Input, false, ct),
            _ => new(Error: "validation_failed")
        };
    }
    private static Task<NativeResult> Inspect(IDbContextFactory<AppDbContext> contexts, SyncEditsRequest r, SyncEditInput e, CancellationToken ct)
    {
        var envelope = e switch { SyncContentUpdate u => u.Input.Envelope, SyncRecordCreate c => c.Input.Envelope,
            SyncFolderCreate c => c.Input.Envelope, SyncCollectionCreate c => c.Input.Envelope, SyncProfileCreate c => c.Input.Envelope,
            _ => throw new JsonException() };
        return Update(contexts, r, e.ResourceKind, new(e.ResourceId, envelope, e.EditedAt), true, ct);
    }
    private static async Task<NativeResult> Update(IDbContextFactory<AppDbContext> contexts, SyncEditsRequest r, string kind, SyncContentEditInput input, bool inspect, CancellationToken ct)
    {
        return kind switch
        {
            "record" => Map(await new RecordUpdateStore(contexts).UpdateAsync(new(r.Actor, r.AccessVerifier, input.ResourceId, new(1, input.Envelope, input.EditedAt), true, inspect), ct), x => new(x.RecordId, x.Revision, x.ServerSequence, x.EditedAt)),
            "folder" => Map(await new FolderUpdateStore(contexts).UpdateAsync(new(r.Actor, r.AccessVerifier, input.ResourceId, new(1, input.Envelope, input.EditedAt), true, inspect), ct), x => new(x.FolderId, x.Revision, x.ServerSequence, x.EditedAt)),
            "collection" => Map(await new CollectionUpdateStore(contexts).UpdateAsync(new(r.Actor, r.AccessVerifier, input.ResourceId, new(1, input.Envelope, input.EditedAt), true, inspect), ct), x => new(x.CollectionId, x.Revision, x.ServerSequence, x.EditedAt)),
            "profile" => Map(await new ProfileUpdateStore(contexts).UpdateAsync(new(r.Actor, r.AccessVerifier, input.ResourceId, new(1, input.Envelope, input.EditedAt), true, inspect), ct), x => new(x.ProfileId, x.Revision, x.ServerSequence, x.EditedAt)),
            _ => new(Error: "validation_failed")
        };
    }
    private static string? AuthorityError(Scope? s) => s is null || !s.Active ? "not_found" : !s.Allowed ? "vault_access_denied"
        : s.AccountId == Guid.Empty || s.ProfileId == Guid.Empty || !SyncResourceData.Safe(s.PolicyRevision) || !SyncResourceData.Safe(s.Generation) ? "persistence_unavailable" : null;
    private static string? Recheck(Scope? s, Scope original)
    {
        var error = AuthorityError(s); if (error is not null) return error;
        return s!.InternalId != original.InternalId || s.AccountId != original.AccountId || s.SessionId != original.SessionId
            || s.ProfileInternalId != original.ProfileInternalId || s.ProfileId != original.ProfileId || s.PolicyRevision != original.PolicyRevision
            || s.Generation != original.Generation || !s.ForeignOwners.ToHashSet().SetEquals(original.ForeignOwners) ? "revision_conflict" : null;
    }
    private static async Task<Scope?> ReadScope(AppDbContext db, SyncEditsRequest r, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<Scope>(ScopeSql, new NpgsqlParameter("actor", r.Actor), new NpgsqlParameter("verifier", r.AccessVerifier)).ToListAsync(ct)).SingleOrDefault();
    private sealed class Scope
    {
        public long InternalId { get; set; } public Guid AccountId { get; set; } public bool Active { get; set; } public bool Allowed { get; set; }
        public long? SessionId { get; set; } public long? ProfileInternalId { get; set; } public Guid? ProfileId { get; set; }
        public long PolicyRevision { get; set; } public long Generation { get; set; } public DateTimeOffset DatabaseNow { get; set; }
        public Guid[] ForeignOwners { get; set; } = [];
    }
    private const string ScopeSql = """
        SELECT a.id internal_id,a.public_id account_id,a.policy_revision,a.revocation_generation generation,statement_timestamp() database_now,
          a.state=0 AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id) active,
          COALESCE(s.id IS NOT NULL AND NOT s.revoked AND s.policy_revision>0 AND s.revocation_generation>0
            AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation AND s.issued_at<=statement_timestamp()
            AND CASE WHEN a.renewal_enabled THEN s.expires_at>statement_timestamp() ELSE s.expires_at IS NULL END
            AND (s.profile_id IS NULL OR (p.id IS NOT NULL AND p.deleted_at IS NULL AND p.purge_at IS NULL
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id))),false) allowed,
          s.id session_id,s.profile_id profile_internal_id,p.public_id profile_id,
          ARRAY(SELECT DISTINCT owner.public_id FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id
            JOIN cerberus.account owner ON owner.id=c.account_id WHERE g.recipient_account_id=a.id AND owner.id<>a.id
              AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_collection pc WHERE pc.profile_id=s.profile_id AND pc.collection_id=c.id))
            ORDER BY owner.public_id) foreign_owners
        FROM cerberus.account a LEFT JOIN cerberus.vault_access_session s ON s.account_id=a.id AND s.handle_verifier=@verifier
        LEFT JOIN cerberus.profile p ON p.id=s.profile_id AND p.account_id=a.id WHERE a.heimdall_public_id=@actor
        """;
}
