using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Records;

public sealed class RecordCreateStore(IDbContextFactory<AppDbContext> factory) : IRecordCreateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<VaultResult<RecordCreateDetails>> CreateAsync(RecordCreateRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        var input = request.Input;
        if (input?.IsValid() != true) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE", ct);
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.HeimdallPublicId == request.Actor, ct);
            if (account is null || account.State != AccountState.Active || await db.TerminalErasures.AnyAsync(x => x.ResourceId == account.PublicId, ct))
                return new(Error: "not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE", ct);
            var session = await db.VaultAccessSessions.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id && x.HandleVerifier == request.AccessVerifier, ct);
            async Task<bool> Permitted()
            {
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
                return session is not null && !session.Revoked && session.PolicyRevision > 0 && session.RevocationGeneration > 0
                    && session.PolicyRevision == account.PolicyRevision && session.RevocationGeneration == account.RevocationGeneration
                    && session.IssuedAt <= now && (account.RenewalEnabled ? session.ExpiresAt > now : session.ExpiresAt is null)
                    && (session.ProfileId is null || await db.Profiles.AnyAsync(p => p.Id == session.ProfileId && p.AccountId == account.Id
                        && p.DeletedAt == null && !db.TerminalErasures.Any(e => e.ResourceId == p.PublicId), ct));
            }
            if (!await Permitted()) return new(Error: "vault_access_denied");
            if (session!.ProfileId is not null && input.ProfileIds.Length == 0) return new(Error: "vault_access_denied");
            var profiles = new List<Profile>();
            foreach (var id in input.ProfileIds.Order())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.profile WHERE account_id={account.Id} AND public_id={id} FOR UPDATE", ct);
                if (!await Permitted()) return new(Error: "vault_access_denied");
                var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id && x.PublicId == id, ct);
                if (profile is null || profile.DeletedAt is not null || await db.TerminalErasures.AnyAsync(x => x.ResourceId == profile.PublicId, ct)
                    || session.ProfileId is not null && (input.ProfileIds.Length != 1 || session.ProfileId != profile.Id))
                    return new(Error: "not_found");
                profiles.Add(profile);
            }
            var folders = new List<VaultFolder>();
            if (input.FolderId is not null)
            {
                var folder = await db.Folders.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id && x.PublicId == input.FolderId, ct);
                if (folder is null) return new(Error: "not_found");
                var visited = new HashSet<long>();
                while (true)
                {
                    if (!visited.Add(folder.Id)) return new(Error: "persistence_unavailable");
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.folder WHERE account_id={account.Id} AND id={folder.Id} FOR UPDATE", ct);
                    if (!await Permitted()) return new(Error: "vault_access_denied");
                    folder = await db.Folders.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id && x.Id == folder.Id, ct);
                    if (folder is null || folder.DeletedAt is not null || await db.TerminalErasures.AnyAsync(x => x.ResourceId == folder.PublicId, ct))
                        return new(Error: "not_found");
                    folders.Add(folder);
                    if (folder.ParentFolderId is null) break;
                    folder = await db.Folders.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id && x.Id == folder.ParentFolderId, ct);
                    if (folder is null) return new(Error: "not_found");
                }
                var ancestry = folders.Select(x => x.Id).ToArray();
                if (session.ProfileId is not null && !await db.ProfileFolders.AnyAsync(x => x.ProfileId == session.ProfileId && x.AccountId == account.Id && ancestry.Contains(x.FolderId), ct))
                    return new(Error: "not_found");
            }
            if (!await Permitted()) return new(Error: "vault_access_denied");
            foreach (var profile in profiles)
            {
                if (!Safe(profile.Revision) || !Safe(profile.ServerSequence)) return new(Error: "persistence_unavailable");
                if (profile.Revision == ProtocolBinary.MaxInteger) return new(Error: "revision_conflict");
            }
            var parent = folders.FirstOrDefault();
            if (parent is not null)
            {
                if (!Safe(parent.Revision) || !Safe(parent.ServerSequence)) return new(Error: "persistence_unavailable");
                if (parent.Revision == ProtocolBinary.MaxInteger) return new(Error: "revision_conflict");
            }
            if (await db.Records.AnyAsync(x => x.PublicId == input.RecordId, ct) || await db.TerminalErasures.AnyAsync(x => x.ResourceId == input.RecordId, ct))
                return new(Error: "revision_conflict");
            var envelope = JsonSerializer.SerializeToUtf8Bytes(input.Envelope, Json);
            var editedAt = input.EditedAt.AddTicks(-(input.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond));
            var folderIds = folders.Select(x => x.Id).ToArray();
            long? parentId = parent?.Id;
            // All owned writers take the account lock. This statement also checks authority and
            // lifecycle at database time, after lock waits and validation, before any write.
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cerberus.record(public_id,account_id,envelope,revision,edited_at,folder_id,concurrency_stamp)
                SELECT {input.RecordId},a.id,{envelope},1,{editedAt},{parentId},{Guid.NewGuid()}
                FROM cerberus.account a JOIN cerberus.vault_access_session s ON s.account_id=a.id
                WHERE a.id={account.Id} AND a.heimdall_public_id={request.Actor} AND a.state={AccountState.Active}
                    AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE e.resource_id=a.public_id OR e.resource_id={input.RecordId})
                    AND s.handle_verifier={request.AccessVerifier} AND NOT s.revoked AND s.policy_revision>0 AND s.revocation_generation>0
                    AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation AND s.issued_at<=statement_timestamp()
                    AND ((a.renewal_enabled AND s.expires_at>statement_timestamp()) OR (NOT a.renewal_enabled AND s.expires_at IS NULL))
                    AND (s.profile_id IS NULL OR (cardinality({input.ProfileIds})=1 AND EXISTS(
                        SELECT 1 FROM cerberus.profile p WHERE p.id=s.profile_id AND p.account_id=a.id AND p.public_id=ANY({input.ProfileIds})
                        AND p.deleted_at IS NULL AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE e.resource_id=p.public_id))
                        AND (cardinality({folderIds})=0 OR EXISTS(SELECT 1 FROM cerberus.profile_folder pf
                            WHERE pf.account_id=a.id AND pf.profile_id=s.profile_id AND pf.folder_id=ANY({folderIds})))))
                    AND (SELECT count(*) FROM cerberus.profile p WHERE p.account_id=a.id AND p.public_id=ANY({input.ProfileIds})
                        AND p.deleted_at IS NULL AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE e.resource_id=p.public_id))=cardinality({input.ProfileIds})
                    AND (SELECT count(*) FROM cerberus.folder f WHERE f.account_id=a.id AND f.id=ANY({folderIds})
                        AND f.deleted_at IS NULL AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE e.resource_id=f.public_id))=cardinality({folderIds})
                """, ct);
            if (inserted != 1)
            {
                if (!await Permitted()) return new(Error: "vault_access_denied");
                if (await db.TerminalErasures.AnyAsync(x => x.ResourceId == input.RecordId, ct)) return new(Error: "revision_conflict");
                return new(Error: "not_found");
            }
            var record = await db.Records.AsNoTracking().SingleAsync(x => x.PublicId == input.RecordId, ct);
            if (record.Revision != 1 || !Safe(record.ServerSequence) || record.EditedAt != editedAt) return new(Error: "persistence_unavailable");
            db.ProfileRecords.AddRange(profiles.Select(p => new ProfileRecord { AccountId = account.Id, ProfileId = p.Id, RecordId = record.Id }));
            await db.SaveChangesAsync(ct);
            foreach (var profile in profiles)
            {
                if (await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE cerberus.profile SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={Guid.NewGuid()}
                    WHERE id={profile.Id} AND account_id={account.Id} AND revision={profile.Revision}
                    """, ct) != 1) return new(Error: "persistence_unavailable");
                var current = await db.Profiles.AsNoTracking().SingleAsync(x => x.Id == profile.Id, ct);
                if (current.Revision != profile.Revision + 1 || !Safe(current.ServerSequence) || current.ServerSequence <= profile.ServerSequence)
                    return new(Error: "persistence_unavailable");
            }
            if (parent is not null)
            {
                if (await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE cerberus.folder SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={Guid.NewGuid()}
                    WHERE id={parent.Id} AND account_id={account.Id} AND revision={parent.Revision}
                    """, ct) != 1) return new(Error: "persistence_unavailable");
                var current = await db.Folders.AsNoTracking().SingleAsync(x => x.Id == parent.Id, ct);
                if (current.Revision != parent.Revision + 1 || !Safe(current.ServerSequence) || current.ServerSequence <= parent.ServerSequence)
                    return new(Error: "persistence_unavailable");
            }
            await tx.CommitAsync(ct);
            return new(new(record.PublicId, record.Revision, record.ServerSequence, record.EditedAt));
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or "2200H") { return new(Error: "revision_conflict"); }
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException }) { return new(Error: "persistence_unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
    }

    private static bool Safe(long value) => value is > 0 and <= ProtocolBinary.MaxInteger;
}
