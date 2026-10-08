using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Data.Relational.Core.Configuration;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : BaseDbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<VaultAccessSession> VaultAccessSessions => Set<VaultAccessSession>();
    public DbSet<VaultProtection> VaultProtections => Set<VaultProtection>();
    public DbSet<VaultUnlockChallenge> VaultUnlockChallenges => Set<VaultUnlockChallenge>();
    public DbSet<RegistrationOperation> RegistrationOperations => Set<RegistrationOperation>();
    public DbSet<RetentionWorkItem> RetentionWorkItems => Set<RetentionWorkItem>();
    public DbSet<TerminalErasure> TerminalErasures => Set<TerminalErasure>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSnakeCaseNamingConvention().EnableDetailedErrors(false).EnableSensitiveDataLogging(false);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("cerberus");
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
        erasure.ToTable("terminal_erasure");
        erasure.HasIndex(x => x.ResourceId).IsUnique();
        erasure.Property(x => x.ResourceKind).HasMaxLength(16).IsRequired();
    }
}
