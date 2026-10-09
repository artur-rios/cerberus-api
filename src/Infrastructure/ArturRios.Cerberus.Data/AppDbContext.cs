using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Data.Configuration;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Data.Relational.Core.Configuration;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : BaseDbContext(options)
{
    public DbSet<VaultRecord> Records => Set<VaultRecord>();
    public DbSet<VaultFolder> Folders => Set<VaultFolder>();
    public DbSet<VaultCollection> Collections => Set<VaultCollection>();
    public DbSet<CollectionGrant> CollectionGrants => Set<CollectionGrant>();
    public DbSet<CollectionRecord> CollectionRecords => Set<CollectionRecord>();
    public DbSet<CollectionFolder> CollectionFolders => Set<CollectionFolder>();
    public DbSet<ProfileRecord> ProfileRecords => Set<ProfileRecord>();
    public DbSet<ProfileFolder> ProfileFolders => Set<ProfileFolder>();
    public DbSet<ProfileCollection> ProfileCollections => Set<ProfileCollection>();
    public DbSet<TrashOperation> TrashOperations => Set<TrashOperation>();
    public DbSet<TrashEntry> TrashEntries => Set<TrashEntry>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<VaultAccessSession> VaultAccessSessions => Set<VaultAccessSession>();
    public DbSet<VaultProtection> VaultProtections => Set<VaultProtection>();
    public DbSet<VaultUnlockChallenge> VaultUnlockChallenges => Set<VaultUnlockChallenge>();
    public DbSet<VaultRecoveryOperation> VaultRecoveryOperations => Set<VaultRecoveryOperation>();
    public DbSet<RegistrationOperation> RegistrationOperations => Set<RegistrationOperation>();
    public DbSet<RetentionWorkItem> RetentionWorkItems => Set<RetentionWorkItem>();
    public DbSet<TerminalErasure> TerminalErasures => Set<TerminalErasure>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSnakeCaseNamingConvention().EnableDetailedErrors(false).EnableSensitiveDataLogging(false);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("cerberus");
        ResourceModel.Configure(modelBuilder);
        modelBuilder.HasSequence<long>("server_sequence", "cerberus").StartsAt(1).IncrementsBy(1).HasMax(ProtocolBinary.MaxInteger);
        var profile=modelBuilder.Entity<Profile>();
        profile.ToTable("profile");
        profile.HasIndex(x=>x.PublicId).IsUnique();
        profile.HasIndex(x=>new{x.AccountId,x.ServerSequence});
        profile.Property(x=>x.Envelope).IsRequired();
        profile.Property(x=>x.KeyWrappers).IsRequired();
        profile.Property(x=>x.Revision).IsConcurrencyToken();
        profile.Property(x=>x.ServerSequence).HasDefaultValueSql("nextval('cerberus.server_sequence')");
        profile.HasOne<Account>().WithMany().HasForeignKey(x=>x.AccountId).OnDelete(DeleteBehavior.Cascade);
        var trash = modelBuilder.Entity<TrashOperation>();
        trash.ToTable("trash_operation");
        trash.HasIndex(x=>x.PublicId).IsUnique();
        trash.HasIndex(x=>new{x.AccountId,x.PurgeAt});
        trash.Property(x=>x.RootResourceKind).HasMaxLength(16).IsRequired();
        trash.HasOne<Account>().WithMany().HasForeignKey(x=>x.AccountId).OnDelete(DeleteBehavior.Cascade);
        var entry = modelBuilder.Entity<TrashEntry>();
        entry.ToTable("trash_entry");
        entry.HasIndex(x=>new{x.ResourceKind,x.ResourceId}).IsUnique();
        entry.Property(x=>x.ResourceKind).HasMaxLength(16).IsRequired();
        entry.Property(x=>x.AssociationSnapshot).IsRequired();
        entry.HasOne<TrashOperation>().WithMany().HasForeignKey(x=>x.OperationId).OnDelete(DeleteBehavior.Cascade);
        var account = modelBuilder.Entity<Account>();
        account.ToTable("account");
        account.HasIndex(x => x.PublicId).IsUnique();
        account.HasIndex(x => x.HeimdallPublicId).IsUnique();
        account.Property(x => x.DetailsEnvelope).IsRequired();
        account.Property(x => x.Revision).IsConcurrencyToken();
        var access = modelBuilder.Entity<VaultAccessSession>();
        access.ToTable("vault_access_session");
        access.HasIndex(x => x.HandleVerifier).IsUnique();
        access.Property(x => x.HandleVerifier).HasMaxLength(64).IsRequired();
        access.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        var protection = modelBuilder.Entity<VaultProtection>();
        protection.ToTable("vault_protection");
        protection.HasIndex(x => x.AccountId).IsUnique();
        protection.Property(x => x.Material).IsRequired();
        protection.Property(x => x.Revision).IsConcurrencyToken();
        protection.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        var challenge = modelBuilder.Entity<VaultUnlockChallenge>();
        challenge.ToTable("vault_unlock_challenge");
        challenge.HasIndex(x => x.PublicId).IsUnique();
        challenge.HasIndex(x => new { x.AccountId, x.ExpiresAt });
        challenge.Property(x => x.Challenge).IsRequired();
        challenge.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        var recovery = modelBuilder.Entity<VaultRecoveryOperation>();
        recovery.ToTable("vault_recovery_operation");
        recovery.HasIndex(x => new { x.AccountId, x.IdempotencyKey }).IsUnique();
        recovery.Property(x => x.RequestHash).HasMaxLength(43).IsRequired();
        recovery.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        var registration = modelBuilder.Entity<RegistrationOperation>();
        registration.ToTable("registration_operation");
        registration.HasIndex(x => x.OperationId).IsUnique();
        registration.Property(x => x.RequestFingerprint).HasMaxLength(64).IsRequired();
        var work = modelBuilder.Entity<RetentionWorkItem>();
        work.ToTable("retention_work_item");
        work.HasIndex(x => x.PublicId).IsUnique();
        work.HasIndex(x => x.OperationKey).IsUnique();
        work.Property(x => x.OperationKey).HasMaxLength(128).IsRequired();
        var erasure = modelBuilder.Entity<TerminalErasure>();
        erasure.ToTable("terminal_erasure", table => table.HasCheckConstraint("ck_terminal_erasure_kind",
            "resource_kind IN ('account','profile','record','folder','collection','grant')"));
        erasure.HasIndex(x => new { x.ResourceKind, x.ResourceId }).IsUnique();
        erasure.Property(x => x.ResourceKind).HasMaxLength(16).IsRequired();
    }
}
