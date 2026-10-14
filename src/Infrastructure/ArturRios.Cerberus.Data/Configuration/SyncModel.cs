using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Configuration;

internal static class SyncModel
{
    internal static void Configure(ModelBuilder model)
    {
        var stream = model.Entity<SyncStream>();
        stream.ToTable("sync_stream", t => t.HasCheckConstraint("ck_sync_stream_checkpoint",
            "window_version BETWEEN 1 AND 9007199254740991 AND last_after>=0 AND boundary>=last_after AND boundary<=9007199254740991 AND page_size>0 AND expires_at>created_at"));
        stream.HasIndex(x => x.PublicId).IsUnique();
        stream.HasIndex(x => x.ExpiresAt);
        stream.Property(x => x.WindowVersion).IsConcurrencyToken();
        stream.Property(x => x.VisibilityStamp).IsRequired();
        stream.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        stream.HasOne<Profile>().WithMany().HasForeignKey(x => new { x.AccountId, x.ProfileId })
            .HasPrincipalKey(x => new { x.AccountId, x.Id }).OnDelete(DeleteBehavior.Cascade);

        const string kinds = "resource_kind IN('profile','record','folder','collection','grant')";
        var inventory = model.Entity<SyncInventory>();
        inventory.ToTable("sync_inventory", t => t.HasCheckConstraint("ck_sync_inventory_metadata", kinds + " AND octet_length(fingerprint)=32"));
        inventory.HasIndex(x => new { x.StreamId, x.ResourceKind, x.ResourceId }).IsUnique();
        inventory.Property(x => x.ResourceKind).HasMaxLength(16).IsRequired();
        inventory.Property(x => x.Fingerprint).IsRequired();
        inventory.HasOne<SyncStream>().WithMany().HasForeignKey(x => x.StreamId).OnDelete(DeleteBehavior.Cascade);

        var change = model.Entity<SyncChange>();
        change.ToTable("sync_change", t => t.HasCheckConstraint("ck_sync_change_descriptor", kinds
            + " AND window_version BETWEEN 1 AND 9007199254740991 AND sequence BETWEEN 1 AND 9007199254740991"
            + " AND event_type IN('upsert','removed','permanently-deleted') AND (event_type='permanently-deleted')=(deleted_at IS NOT NULL)"));
        change.Property(x => x.Sequence).HasDefaultValueSql("nextval('cerberus.server_sequence')");
        change.HasIndex(x => x.Sequence).IsUnique();
        change.HasIndex(x => new { x.StreamId, x.WindowVersion, x.Sequence });
        change.HasIndex(x => new { x.StreamId, x.WindowVersion, x.ResourceKind, x.ResourceId }).IsUnique();
        change.Property(x => x.ResourceKind).HasMaxLength(16).IsRequired();
        change.Property(x => x.EventType).HasMaxLength(24).IsRequired();
        change.HasOne<SyncStream>().WithMany().HasForeignKey(x => x.StreamId).OnDelete(DeleteBehavior.Cascade);

        var operation = model.Entity<SyncOperation>();
        operation.ToTable("sync_operation", t => t.HasCheckConstraint("ck_sync_operation_metadata", """
            operation_id<>'00000000-0000-0000-0000-000000000000'::uuid
            AND resource_id<>'00000000-0000-0000-0000-000000000000'::uuid
            AND resource_kind IN('profile','record','folder','collection') AND action IN('create','update')
            AND octet_length(request_fingerprint)=32
            AND ((status IN('applied','superseded') AND revision IS NOT NULL AND server_sequence IS NOT NULL
              AND revision BETWEEN 1 AND 9007199254740991 AND server_sequence BETWEEN 1 AND 9007199254740991 AND edited_at IS NOT NULL)
              OR (status IN('denied','permanently-deleted') AND revision IS NULL AND server_sequence IS NULL AND edited_at IS NULL))
            """));
        operation.HasIndex(x => new { x.AccountId, x.OperationId }).IsUnique();
        operation.HasIndex(x => new { x.AccountId, x.ScopeProfileId, x.ResourceKind, x.ResourceId });
        operation.Property(x => x.RequestFingerprint).IsRequired();
        operation.Property(x => x.ResourceKind).HasMaxLength(16).IsRequired();
        operation.Property(x => x.Action).HasMaxLength(16).IsRequired();
        operation.Property(x => x.Status).HasMaxLength(24).IsRequired();
        operation.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}
