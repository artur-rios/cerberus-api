using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Shared.Configuration;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Sync;

public sealed class SyncDownloadStore(IDbContextFactory<AppDbContext> factory, CerberusOptions options) : ISyncDownloadStore
{
    public async Task<SyncDownloadResult> DownloadAsync(SyncDownloadRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request is null) return new(Error: "validation_failed");
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (string.IsNullOrWhiteSpace(request.AccessVerifier)) return new(Error: "vault_access_required");
        if (request.PageSize <= 0 || request.PageSize > options.MaxPageSize) return new(Error: "validation_failed");
        if (!TimeSpan.TryParseExact(options.SyncRetention, "c", CultureInfo.InvariantCulture, out var retention) || retention <= TimeSpan.Zero)
            return new(Error: "persistence_unavailable");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var initial = await ReadScope(db, request, ct);
            var error = AuthorityError(initial); if (error is not null) return new(Error: error);
            var original = initial!;
            var accounts = original.ForeignOwners.Append(original.AccountId).Distinct().ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE public_id=ANY({accounts}) ORDER BY public_id FOR SHARE", ct);
            error = RecheckError(await ReadScope(db, request, ct), original); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE id={original.SessionId} AND account_id={original.OwnerId} FOR SHARE", ct);
            error = RecheckError(await ReadScope(db, request, ct), original); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE id={original.ProfileInternalId} AND account_id={original.OwnerId} FOR SHARE", ct);
            var lockedScope = await ReadScope(db, request, ct);
            error = RecheckError(lockedScope, original); if (error is not null) return new(Error: error);
            var scope = lockedScope!;
            SyncDownloadResult Reset() => new(new(scope.AccountId, scope.ProfileId, scope.PolicyRevision, scope.Generation,
                scope.DatabaseNow, [], null, false, true, true));
            if (request.InvalidCursor || request.Checkpoint is { } invalid && (!invalid.IsValid() || invalid.ProfileId != scope.ProfileId)) return Reset();

            SyncStream stream;
            var fresh = request.Checkpoint is null;
            if (fresh)
            {
                if (retention > DateTimeOffset.MaxValue - scope.DatabaseNow) return new(Error: "persistence_unavailable");
                stream = new() { PublicId = Guid.NewGuid(), AccountId = scope.OwnerId, ProfileId = scope.ProfileInternalId,
                    CreatedAt = scope.DatabaseNow, ExpiresAt = scope.DatabaseNow + retention, PageSize = request.PageSize };
                db.SyncStreams.Add(stream); await db.SaveChangesAsync(ct);
            }
            else
            {
                var cp = request.Checkpoint!;
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.sync_stream WHERE public_id={cp.StreamId} AND account_id={scope.OwnerId} AND profile_id={scope.ProfileInternalId} FOR UPDATE", ct);
                scope = (await ReadScope(db, request, ct))!;
                error = RecheckError(scope, original); if (error is not null) return new(Error: error);
                var saved = await db.SyncStreams.SingleOrDefaultAsync(x => x.PublicId == cp.StreamId && x.AccountId == scope.OwnerId && x.ProfileId == scope.ProfileInternalId, ct);
                if (saved is null) return Reset();
                stream = saved;
                if (stream.ExpiresAt <= scope.DatabaseNow)
                {
                    db.SyncStreams.Remove(stream); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Reset();
                }
                if (stream.CreatedAt > scope.DatabaseNow || stream.ExpiresAt <= stream.CreatedAt || stream.PublicId == Guid.Empty
                    || !SyncResourceData.Safe(stream.WindowVersion) || stream.Boundary is < 0 or > ProtocolBinary.MaxInteger
                    || stream.LastAfter < 0 || stream.LastAfter > stream.Boundary || stream.VisibilityStamp.Length != 32
                    || stream.WindowComplete != (stream.LastAfter == stream.Boundary))
                    return new(Error: "persistence_unavailable");
                if (stream.PageSize != request.PageSize || cp.WindowVersion != stream.WindowVersion || cp.Boundary != stream.Boundary
                    || cp.After > stream.LastAfter || stream.WindowComplete && stream.LastAfter != stream.Boundary) return Reset();
            }
            // Shared recipient writers lock admitted collections FOR SHARE before
            // editing payload/sequence. Account roots alone do not serialize those
            // writers; retain conflicting collection locks across the whole scan.
            // Lock every collection of these owners: another recipient can edit
            // through a collection outside this profile (including direct own data).
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT FROM cerberus.collection c JOIN cerberus.account owner ON owner.id=c.account_id
                WHERE owner.public_id=ANY({accounts})
                ORDER BY c.public_id FOR NO KEY UPDATE OF c
                """, ct);
            scope = (await ReadScope(db, request, ct))!;
            error = RecheckError(scope, original); if (error is not null) return new(Error: error);
            var current = await CurrentSyncInventory.ReadAsync(db, factory, request, scope.OwnerId, scope.ProfileId, ct);
            if (current.Error is not null) return new(Error: current.Error);
            var visible = current.Items!;
            var visibility = VisibilityStamp(scope, visible);
            if (!fresh && (!stream.WindowComplete || request.Checkpoint!.After < stream.Boundary)
                && !CryptographicOperations.FixedTimeEquals(stream.VisibilityStamp, visibility)) return Reset();
            var known = await db.SyncInventories.Where(x => x.StreamId == stream.Id).ToListAsync(ct);
            if (known.Any(x => x.Fingerprint.Length != 32 || !TerminalResourceIdentity(x.ResourceKind, x.ResourceId)
                || x.Terminal && x.Visible)) return new(Error: "persistence_unavailable");
            var inventory = known.ToDictionary(x => new SyncResourceIdentity(x.ResourceKind, x.ResourceId));
            var startWindow = fresh || stream.WindowComplete && request.Checkpoint!.After == stream.Boundary;
            long after = fresh ? 0 : request.Checkpoint!.After;
            if (startWindow)
            {
                var previousBoundary = stream.Boundary;
                var ids = known.Select(x => x.ResourceId).ToArray();
                var terminals = await db.TerminalErasures.AsNoTracking().Where(x => ids.Contains(x.ResourceId)).ToListAsync(ct);
                var erased = terminals.ToDictionary(x => new SyncResourceIdentity(x.ResourceKind, x.ResourceId));
                var removals = new List<SyncChange>();
                foreach (var k in known)
                {
                    var identity = new SyncResourceIdentity(k.ResourceKind, k.ResourceId);
                    if (visible.ContainsKey(identity)) continue;
                    if (erased.TryGetValue(identity, out var terminal) && !k.Terminal)
                        removals.Add(new() { ResourceKind = k.ResourceKind, ResourceId = k.ResourceId, EventType = "permanently-deleted", DeletedAt = terminal.DeletedAt });
                    else if (k.Visible) removals.Add(new() { ResourceKind = k.ResourceKind, ResourceId = k.ResourceId, EventType = "removed" });
                }
                var upserts = visible.Where(x => !inventory.TryGetValue(x.Key, out var previous) || !previous.Visible
                    || !CryptographicOperations.FixedTimeEquals(previous.Fingerprint, CurrentSyncInventory.Fingerprint(x.Value)))
                    .Select(x => new SyncChange { ResourceKind = x.Key.Kind, ResourceId = x.Key.Id, EventType = "upsert" }).ToList();
                if (!fresh && removals.Count + upserts.Count > 0)
                {
                    if (stream.WindowVersion == ProtocolBinary.MaxInteger) return Reset();
                    stream.WindowVersion++;
                }
                stream.VisibilityStamp = visibility;
                if (removals.Count + upserts.Count > 0)
                {
                    foreach (var change in removals.Concat(upserts)) { change.StreamId = stream.Id; change.WindowVersion = stream.WindowVersion; }
                    // Separate save phases guarantee all removal sequences precede
                    // upserts, independent of provider batch insertion ordering.
                    db.SyncChanges.AddRange(removals); await db.SaveChangesAsync(ct);
                    db.SyncChanges.AddRange(upserts); await db.SaveChangesAsync(ct);
                    var generated = removals.Concat(upserts).Select(x => x.Sequence).ToArray();
                    if (generated.Any(x => !SyncResourceData.Safe(x) || x <= previousBoundary)
                        || generated.Distinct().Count() != generated.Length
                        || removals.Count > 0 && upserts.Count > 0 && removals.Max(x => x.Sequence) >= upserts.Min(x => x.Sequence))
                        return new(Error: "persistence_unavailable");
                    stream.Boundary = generated.Max();
                    stream.LastAfter = 0; stream.WindowComplete = false; after = 0;
                }
                else { stream.WindowComplete = true; stream.LastAfter = stream.Boundary; after = stream.Boundary; }
            }
            var descriptors = await db.SyncChanges.Where(x => x.StreamId == stream.Id && x.WindowVersion == stream.WindowVersion
                && x.Sequence > after && x.Sequence <= stream.Boundary).OrderBy(x => x.Sequence).Take(request.PageSize + 1).ToListAsync(ct);
            var more = descriptors.Count > request.PageSize;
            var items = new List<SyncChangeItem>();
            foreach (var d in descriptors.Take(request.PageSize))
            {
                var identity = new SyncResourceIdentity(d.ResourceKind, d.ResourceId);
                SyncResourceData? data = null;
                if (d.EventType == "upsert" && !visible.TryGetValue(identity, out data)) return Reset();
                var item = new SyncChangeItem(d.Sequence, d.ResourceKind, d.ResourceId, d.EventType, data, d.DeletedAt);
                if (!item.IsValid(request.Actor, scope.ProfileId)) return new(Error: "persistence_unavailable");
                items.Add(item);
                if (!inventory.TryGetValue(identity, out var entry))
                {
                    if (data is null) return new(Error: "persistence_unavailable");
                    entry = new() { StreamId = stream.Id, ResourceKind = d.ResourceKind, ResourceId = d.ResourceId, Fingerprint = CurrentSyncInventory.Fingerprint(data) };
                    db.SyncInventories.Add(entry); inventory.Add(identity, entry);
                }
                if (data is not null)
                {
                    if (entry.Terminal) return new(Error: "persistence_unavailable");
                    entry.Fingerprint = CurrentSyncInventory.Fingerprint(data); entry.Visible = true;
                }
                else { entry.Visible = false; entry.Terminal |= d.EventType == "permanently-deleted"; }
            }
            var last = items.Count > 0 ? items[^1].Sequence : after;
            if (!more && last != stream.Boundary) return new(Error: "persistence_unavailable");
            stream.LastAfter = Math.Max(stream.LastAfter, last); stream.WindowComplete = stream.LastAfter == stream.Boundary;
            await db.SaveChangesAsync(ct);
            var final = await ReadScope(db, request, ct);
            error = RecheckError(final, original); if (error is not null) return new(Error: error);
            if (stream.ExpiresAt <= final!.DatabaseNow) { scope = final; return Reset(); }
            await tx.CommitAsync(ct);
            return new(new(final!.AccountId, final.ProfileId, final.PolicyRevision, final.Generation, final.DatabaseNow, items,
                new(final.ProfileId, stream.PublicId, stream.WindowVersion, last, stream.Boundary), more, fresh, false));
        }
        catch (Exception e) when (e is PostgresException { SqlState: "2200H" }
            or DbUpdateException { InnerException: PostgresException { SqlState: "2200H" } }) { return new(Error: "revision_conflict"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (DbUpdateConcurrencyException) { return new(Error: "revision_conflict"); }
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

    private static bool TerminalResourceIdentity(string kind, Guid id) => id != Guid.Empty && kind is "profile" or "record" or "folder" or "collection" or "grant";
    private static byte[] VisibilityStamp(Scope s, Dictionary<SyncResourceIdentity, SyncResourceData> items) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        s.ProfileId, s.ProfileRevision, s.PolicyRevision, s.Generation,
        resources = items.OrderBy(x => x.Key.Kind, StringComparer.Ordinal).ThenBy(x => x.Key.Id.ToString("D"), StringComparer.Ordinal)
            .Select(x => new { x.Key.Kind, x.Key.Id, grantRevision = x.Value is SyncGrantData g ? g.Revision : 0 })
    }, CurrentSyncInventory.Json));
    private static string? AuthorityError(Scope? s)
    {
        if (s is null || !s.ActorActive) return "not_found";
        if (!s.Allowed) return "vault_access_denied";
        if (s.AccountId == Guid.Empty || s.ProfileId == Guid.Empty || !SyncResourceData.Safe(s.PolicyRevision)
            || !SyncResourceData.Safe(s.Generation) || !SyncResourceData.Safe(s.ProfileRevision)) return "persistence_unavailable";
        return null;
    }
    private static string? RecheckError(Scope? current, Scope captured)
    {
        var error = AuthorityError(current); if (error is not null) return error;
        return current!.OwnerId != captured.OwnerId || current.AccountId != captured.AccountId || current.SessionId != captured.SessionId
            || current.ProfileInternalId != captured.ProfileInternalId || current.ProfileId != captured.ProfileId
            || current.ProfileRevision != captured.ProfileRevision || current.PolicyRevision != captured.PolicyRevision
            || current.Generation != captured.Generation || !current.ForeignOwners.ToHashSet().SetEquals(captured.ForeignOwners)
            ? "revision_conflict" : null;
    }
    private static async Task<Scope?> ReadScope(AppDbContext db, SyncDownloadRequest r, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<Scope>(ScopeSql, new NpgsqlParameter("actor", r.Actor), new NpgsqlParameter("verifier", r.AccessVerifier)).ToListAsync(ct)).SingleOrDefault();
    private sealed class Scope
    {
        public long OwnerId { get; set; } public Guid AccountId { get; set; } public bool ActorActive { get; set; } public bool Allowed { get; set; }
        public long? SessionId { get; set; } public long ProfileInternalId { get; set; } public Guid ProfileId { get; set; } public long ProfileRevision { get; set; }
        public long PolicyRevision { get; set; } public long Generation { get; set; } public DateTimeOffset DatabaseNow { get; set; }
        public Guid[] ForeignOwners { get; set; } = [];
    }
    private const string ScopeSql = """
        SELECT a.id owner_id,a.public_id account_id,a.policy_revision,a.revocation_generation generation,statement_timestamp() database_now,
          a.state=0 AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id) actor_active,
          COALESCE(s.id IS NOT NULL AND NOT s.revoked AND s.policy_revision>0 AND s.revocation_generation>0
            AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
            AND s.issued_at<=statement_timestamp() AND CASE WHEN a.renewal_enabled THEN s.expires_at>statement_timestamp() ELSE s.expires_at IS NULL END
            AND p.id IS NOT NULL AND p.deleted_at IS NULL AND p.purge_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id),false) allowed,
          s.id session_id,COALESCE(p.id,0) profile_internal_id,COALESCE(p.public_id,'00000000-0000-0000-0000-000000000000'::uuid) profile_id,
          COALESCE(p.revision,0) profile_revision,
          ARRAY(SELECT DISTINCT owner.public_id FROM cerberus.profile_collection pc JOIN cerberus.collection c ON c.id=pc.collection_id
            JOIN cerberus.account owner ON owner.id=c.account_id WHERE pc.profile_id=p.id AND owner.id<>a.id ORDER BY owner.public_id) foreign_owners
        FROM cerberus.account a LEFT JOIN cerberus.vault_access_session s ON s.account_id=a.id AND s.handle_verifier=@verifier
        LEFT JOIN cerberus.profile p ON p.id=s.profile_id AND p.account_id=a.id WHERE a.heimdall_public_id=@actor
        """;
}
