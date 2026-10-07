using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Data.Relational.Core.Configuration;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : BaseDbContext(options)
{
    public DbSet<RetentionWorkItem> RetentionWorkItems => Set<RetentionWorkItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSnakeCaseNamingConvention().EnableDetailedErrors(false).EnableSensitiveDataLogging(false);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("cerberus");
        var work = modelBuilder.Entity<RetentionWorkItem>();
        work.ToTable("retention_work_item");
        work.HasIndex(x => x.PublicId).IsUnique();
        work.HasIndex(x => x.OperationKey).IsUnique();
        work.Property(x => x.OperationKey).HasMaxLength(128).IsRequired();
    }
}
