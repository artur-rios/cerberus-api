using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Folders;

public sealed class FolderCreateStore(IDbContextFactory<AppDbContext> factory) : IFolderCreateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<VaultResult<FolderCreateDetails>> CreateAsync(FolderCreateRequest request, CancellationToken ct)
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
            var account = await db.Accounts.AsNoTracking().Where(x => x.HeimdallPublicId == request.Actor)
                .Select(x => new { x.Id, x.PublicId, x.State, x.PolicyRevision, x.RevocationGeneration, x.RenewalEnabled }).SingleOrDefaultAsync(ct);
            if (account is null || account.State != AccountState.Active
                || await db.TerminalErasures.AnyAsync(x => x.ResourceKind == "account" && x.ResourceId == account.PublicId, ct))
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
                        && p.DeletedAt == null && !db.TerminalErasures.Any(e => e.ResourceKind == "profile" && e.ResourceId == p.PublicId), ct));
            }
            if (!await Permitted()) return new(Error: "vault_access_denied");
            if (session!.ProfileId is not null && input.ProfileIds.Length == 0) return new(Error: "vault_access_denied");

            var profiles = new List<ParentMetadata>();
            foreach (var id in input.ProfileIds.Order())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.profile WHERE account_id={account.Id} AND public_id={id} FOR UPDATE", ct);
                if (!await Permitted()) return new(Error: "vault_access_denied");
                var profile = await db.Profiles.AsNoTracking().Where(x => x.AccountId == account.Id && x.PublicId == id
                    && x.DeletedAt == null && !db.TerminalErasures.Any(e => e.ResourceKind == "profile" && e.ResourceId == x.PublicId))
                    .Select(x => new ParentMetadata(x.Id, x.PublicId, x.Revision, x.ServerSequence)).SingleOrDefaultAsync(ct);
                if (profile is null || session.ProfileId is not null && (input.ProfileIds.Length != 1 || session.ProfileId != profile.Id))
                    return new(Error: "not_found");
                profiles.Add(profile);
            }

            async Task<bool> ParentPermitted(long[] ancestry)
            {
                if (session.ProfileId is null) return true;
                if (await db.ProfileFolders.AnyAsync(x => x.AccountId == account.Id && x.ProfileId == session.ProfileId && ancestry.Contains(x.FolderId), ct))
                    return true;
                return await (from pc in db.ProfileCollections
                              join c in db.Collections on pc.CollectionId equals c.Id
                              join cf in db.CollectionFolders on c.Id equals cf.CollectionId
                              where pc.ProfileId == session.ProfileId && c.AccountId == account.Id && cf.AccountId == account.Id
                                  && c.DeletedAt == null && ancestry.Contains(cf.FolderId)
                                  && !db.TerminalErasures.Any(e => e.ResourceKind == "collection" && e.ResourceId == c.PublicId)
                              select c.Id).AnyAsync(ct);
            }
            async Task<FolderMetadata?> Folder(long? id, Guid? publicId)
                => await db.Folders.AsNoTracking().Where(x => x.AccountId == account.Id
                    && (id == null ? x.PublicId == publicId : x.Id == id) && x.DeletedAt == null
                    && !db.TerminalErasures.Any(e => e.ResourceKind == "folder" && e.ResourceId == x.PublicId))
                    .Select(x => new FolderMetadata(x.Id, x.PublicId, x.ParentFolderId, x.Revision, x.ServerSequence)).SingleOrDefaultAsync(ct);

            var folders = new List<FolderMetadata>();
            if (input.ParentFolderId is not null)
            {
                var folder = await Folder(null, input.ParentFolderId);
                if (folder is null) return new(Error: "not_found");
                var visited = new HashSet<long>();
                while (true)
                {
                    if (!visited.Add(folder.Id))
                        return new(Error: await ParentPermitted(folders.Select(x => x.Id).ToArray()) ? "persistence_unavailable" : "not_found");
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.folder WHERE account_id={account.Id} AND id={folder.Id} FOR UPDATE", ct);
                    if (!await Permitted()) return new(Error: "vault_access_denied");
                    folder = await Folder(folder.Id, null);
                    if (folder is null) return new(Error: "not_found");
                    folders.Add(folder);
                    if (folder.ParentFolderId is null) break;
                    folder = await Folder(folder.ParentFolderId, null);
                    if (folder is null) return new(Error: "not_found");
                }
                if (!await ParentPermitted(folders.Select(x => x.Id).ToArray())) return new(Error: "not_found");
            }
            if (!await Permitted()) return new(Error: "vault_access_denied");
            var parent = folders.FirstOrDefault();
            foreach (var metadata in profiles.Select(x => (x.Revision, x.ServerSequence))
                .Concat(parent is null ? [] : new[] { (parent.Revision, parent.ServerSequence) }))
            {
                if (!Safe(metadata.Revision) || !Safe(metadata.ServerSequence)) return new(Error: "persistence_unavailable");
                if (metadata.Revision == ProtocolBinary.MaxInteger) return new(Error: "revision_conflict");
            }
            if (await db.Folders.AnyAsync(x => x.PublicId == input.FolderId, ct)
                || await db.TerminalErasures.AnyAsync(x => x.ResourceKind == "folder" && x.ResourceId == input.FolderId, ct))
                return new(Error: "revision_conflict");

            var envelope = JsonSerializer.SerializeToUtf8Bytes(input.Envelope, Json);
            var editedAt = input.EditedAt.AddTicks(-(input.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond));
            var folderIds = folders.Select(x => x.Id).ToArray();
            var ancestry = JsonSerializer.Serialize(folders.OrderBy(x => x.Id).Select(x => new { id = x.Id, parent = x.ParentFolderId }));
            long? parentId = parent?.Id;
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cerberus.folder(public_id,account_id,envelope,revision,edited_at,parent_folder_id,concurrency_stamp)
                SELECT {input.FolderId},a.id,{envelope},1,{editedAt},{parentId},{Guid.NewGuid()}
                FROM cerberus.account a JOIN cerberus.vault_access_session s ON s.account_id=a.id
                WHERE a.id={account.Id} AND a.heimdall_public_id={request.Actor} AND a.state={AccountState.Active}
                    AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=a.public_id)
                        OR (e.resource_kind='folder' AND e.resource_id={input.FolderId}))
                    AND s.handle_verifier={request.AccessVerifier} AND NOT s.revoked AND s.policy_revision>0 AND s.revocation_generation>0
                    AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation AND s.issued_at<=statement_timestamp()
                    AND ((a.renewal_enabled AND s.expires_at>statement_timestamp()) OR (NOT a.renewal_enabled AND s.expires_at IS NULL))
                    AND (s.profile_id IS NULL OR (cardinality({input.ProfileIds})=1 AND EXISTS(
                        SELECT FROM cerberus.profile p WHERE p.id=s.profile_id AND p.account_id=a.id AND p.public_id=ANY({input.ProfileIds})
                            AND p.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id))
                        AND (cardinality({folderIds})=0 OR EXISTS(SELECT FROM cerberus.profile_folder pf
                            WHERE pf.account_id=a.id AND pf.profile_id=s.profile_id AND pf.folder_id=ANY({folderIds}))
                            OR EXISTS(SELECT FROM cerberus.profile_collection pc JOIN cerberus.collection c ON c.id=pc.collection_id
                                JOIN cerberus.collection_folder cf ON cf.collection_id=c.id AND cf.account_id=a.id
                                WHERE pc.profile_id=s.profile_id AND c.account_id=a.id AND c.deleted_at IS NULL AND cf.folder_id=ANY({folderIds})
                                    AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='collection' AND e.resource_id=c.public_id)))))
                    AND (SELECT count(*) FROM cerberus.profile p WHERE p.account_id=a.id AND p.public_id=ANY({input.ProfileIds})
                        AND p.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id))=cardinality({input.ProfileIds})
                    AND (SELECT count(*) FROM cerberus.folder f WHERE f.account_id=a.id AND f.id=ANY({folderIds})
                        AND f.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id))=cardinality({folderIds})
                    AND (SELECT COALESCE(jsonb_agg(jsonb_build_object('id',f.id,'parent',f.parent_folder_id) ORDER BY f.id),'[]'::jsonb)
                        FROM cerberus.folder f WHERE f.account_id=a.id AND f.id=ANY({folderIds}))={ancestry}::jsonb
                """, ct);
            if (inserted != 1)
            {
                if (await db.TerminalErasures.AnyAsync(x => x.ResourceKind == "account" && x.ResourceId == account.PublicId, ct)) return new(Error: "not_found");
                if (!await Permitted()) return new(Error: "vault_access_denied");
                if (await db.Profiles.CountAsync(x => x.AccountId == account.Id && input.ProfileIds.Contains(x.PublicId)
                    && x.DeletedAt == null && !db.TerminalErasures.Any(e => e.ResourceKind == "profile" && e.ResourceId == x.PublicId), ct) != input.ProfileIds.Length)
                    return new(Error: "not_found");
                foreach (var folder in folders)
                    if (await Folder(folder.Id, null) is null) return new(Error: "not_found");
                if (parent is not null && !await ParentPermitted(folderIds)) return new(Error: "not_found");
                return new(Error: "revision_conflict");
            }
            var created = await db.Folders.AsNoTracking().Where(x => x.PublicId == input.FolderId)
                .Select(x => new FolderCreateDetails(x.PublicId, x.Revision, x.ServerSequence, x.EditedAt)).SingleAsync(ct);
            if (created.Revision != 1 || !Safe(created.ServerSequence) || created.EditedAt != editedAt) return new(Error: "persistence_unavailable");
            var createdId = await db.Folders.Where(x => x.PublicId == input.FolderId).Select(x => x.Id).SingleAsync(ct);
            db.ProfileFolders.AddRange(profiles.Select(p => new ProfileFolder { AccountId = account.Id, ProfileId = p.Id, FolderId = createdId }));
            await db.SaveChangesAsync(ct);
            foreach (var profile in profiles)
            {
                if (await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE cerberus.profile SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={Guid.NewGuid()}
                    WHERE id={profile.Id} AND account_id={account.Id} AND revision={profile.Revision}
                    """, ct) != 1) return new(Error: "persistence_unavailable");
                var current = await db.Profiles.Where(x => x.Id == profile.Id).Select(x => new { x.Revision, x.ServerSequence }).SingleAsync(ct);
                if (current.Revision != profile.Revision + 1 || !Safe(current.ServerSequence) || current.ServerSequence <= profile.ServerSequence)
                    return new(Error: "persistence_unavailable");
            }
            if (parent is not null)
            {
                if (await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE cerberus.folder SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={Guid.NewGuid()}
                    WHERE id={parent.Id} AND account_id={account.Id} AND revision={parent.Revision}
                    """, ct) != 1) return new(Error: "persistence_unavailable");
                var current = await db.Folders.Where(x => x.Id == parent.Id).Select(x => new { x.Revision, x.ServerSequence }).SingleAsync(ct);
                if (current.Revision != parent.Revision + 1 || !Safe(current.ServerSequence) || current.ServerSequence <= parent.ServerSequence)
                    return new(Error: "persistence_unavailable");
            }
            await tx.CommitAsync(ct);
            return new(created);
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or "2200H") { return new(Error: "revision_conflict"); }
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
    }

    private static bool Safe(long value) => value is > 0 and <= ProtocolBinary.MaxInteger;
    private sealed record ParentMetadata(long Id, Guid PublicId, long Revision, long ServerSequence);
    private sealed record FolderMetadata(long Id, Guid PublicId, long? ParentFolderId, long Revision, long ServerSequence);
}
