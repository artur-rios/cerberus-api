using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArturRios.Cerberus.Data.Sync;

internal sealed record SyncResourceIdentity(string Kind, Guid Id);
internal sealed record CurrentSyncInventoryResult(Dictionary<SyncResourceIdentity, SyncResourceData>? Items = null, string? Error = null);

internal static class CurrentSyncInventory
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false };

    internal static byte[] Fingerprint(SyncResourceData item) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(item, Json));

    internal static async Task<CurrentSyncInventoryResult> ReadAsync(AppDbContext db, IDbContextFactory<AppDbContext> contexts, SyncDownloadRequest request,
        long account, Guid profileId, CancellationToken ct)
    {
        var factory = new TransactionFactory(contexts, db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction());
        var items = new Dictionary<SyncResourceIdentity, SyncResourceData>();
        void Add(SyncResourceData data)
        {
            if (!data.IsValid(request.Actor, profileId) || !items.TryAdd(new(data.ResourceKind, data.ResourceId), data))
                throw new JsonException("Invalid current synchronization resource.");
        }
        var profile = await new ProfileReadStore(factory).ReadAsync(new(request.Actor, request.AccessVerifier, profileId), ct);
        if (profile.Error is not null) return new(Error: profile.Error);
        var p = profile.Data?.Profile;
        if (p is null || p.ProfileId != profileId) return new(Error: "persistence_unavailable");
        Add(new SyncProfileData(p.ProfileId, p.Revision, p.ServerSequence, p.EditedAt,
            Decode<EncryptedEnvelope>(p.Envelope), Decode<ArturRios.Cerberus.Domain.Profiles.ProfileKeyWrappers>(p.KeyWrappers),
            profile.Data!.RecordIds, profile.Data.FolderIds, profile.Data.CollectionIds));

        // Reuse native permission, ancestry and grant/protection validation rather
        // than create another definition of visible encrypted content.
        long after = 0; long? boundary = null;
        while (true)
        {
            var result = await new RecordListStore(factory).ListAsync(new(request.Actor, request.AccessVerifier, 100, after, boundary), ct);
            if (result.Error is not null) return new(Error: result.Error);
            var page = result.Data!;
            foreach (var r in page.Items) Add(new SyncRecordData(r.RecordId, r.Revision, r.ServerSequence, r.EditedAt, Decode<EncryptedEnvelope>(r.Envelope)));
            if (!page.HasMore) break;
            if (page.Items.Count == 0 || page.Items[^1].ServerSequence <= after) return new(Error: "persistence_unavailable");
            after = page.Items[^1].ServerSequence; boundary = page.Boundary;
        }
        after = 0; boundary = null;
        while (true)
        {
            var result = await new FolderListStore(factory).ListAsync(new(request.Actor, request.AccessVerifier, 100, after, boundary), ct);
            if (result.Error is not null) return new(Error: result.Error);
            var page = result.Data!;
            foreach (var f in page.Items) Add(new SyncFolderData(f.FolderId, f.Revision, f.ServerSequence, f.EditedAt, Decode<EncryptedEnvelope>(f.Envelope)));
            if (!page.HasMore) break;
            if (page.Items.Count == 0 || page.Items[^1].ServerSequence <= after) return new(Error: "persistence_unavailable");
            after = page.Items[^1].ServerSequence; boundary = page.Boundary;
        }
        after = 0; boundary = null;
        while (true)
        {
            var result = await new CollectionListStore(factory).ListAsync(new(request.Actor, request.AccessVerifier, 100, after, boundary), ct);
            if (result.Error is not null) return new(Error: result.Error);
            var page = result.Data!;
            foreach (var c in page.Items) Add(new SyncCollectionData(c.CollectionId, c.Revision, c.ServerSequence, c.EditedAt, Decode<EncryptedEnvelope>(c.Envelope), c.KeyEpoch));
            if (!page.HasMore) break;
            if (page.Items.Count == 0 || page.Items[^1].ServerSequence <= after) return new(Error: "persistence_unavailable");
            after = page.Items[^1].ServerSequence; boundary = page.Boundary;
        }
        var visibleCollections = items.Keys.Where(x => x.Kind == "collection").Select(x => x.Id).ToArray();
        // CollectionListStore already checked each visible foreign grant's native
        // pins/signature under the account locks. Return its current provisioning.
        var grants = await (from g in db.CollectionGrants.AsNoTracking()
                            join c in db.Collections on g.CollectionId equals c.Id
                            where g.RecipientAccountId == account && c.AccountId != account && visibleCollections.Contains(c.PublicId)
                                && g.State == CollectionGrantState.Active
                                && (g.Access == CollectionGrantAccess.ReadOnly || g.Access == CollectionGrantAccess.ReadWrite)
                                && !db.TerminalErasures.Any(e => e.ResourceKind == "grant" && e.ResourceId == g.PublicId)
                            select new { g.PublicId, g.Revision, CollectionId = c.PublicId, g.RecipientKeyEnvelope }).ToListAsync(ct);
        foreach (var g in grants) Add(new SyncGrantData(g.PublicId, g.Revision, g.CollectionId, Decode<RecipientEnvelope>(g.RecipientKeyEnvelope)));
        return new(items);
    }

    private static T Decode<T>(byte[] bytes) where T : class => JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new JsonException();

    // Child stores dispose only their context. The outer operation owns the
    // already-open connection and transaction, retaining one locked scope.
    private sealed class TransactionFactory(IDbContextFactory<AppDbContext> contexts, DbConnection connection, DbTransaction transaction) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            var context = contexts.CreateDbContext();
            context.Database.SetDbConnection(connection, contextOwnsConnection: false);
            context.Database.UseTransaction(transaction);
            return context;
        }
    }
}
