using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Configuration;

internal static class ResourceModel
{
    internal static void Configure(ModelBuilder model)
    {
        ConfigureResource(model,typeof(VaultRecord),"record");
        ConfigureResource(model,typeof(VaultFolder),"folder");
        ConfigureResource(model,typeof(VaultCollection),"collection");
        model.Entity<Profile>().HasAlternateKey(x=>new{x.AccountId,x.Id});
        model.Entity<VaultRecord>().HasOne<VaultFolder>().WithMany().HasForeignKey(x=>new{x.AccountId,x.FolderId})
            .HasPrincipalKey(x=>new{x.AccountId,x.Id}).OnDelete(DeleteBehavior.NoAction);
        model.Entity<VaultFolder>().HasOne<VaultFolder>().WithMany().HasForeignKey(x=>new{x.AccountId,x.ParentFolderId})
            .HasPrincipalKey(x=>new{x.AccountId,x.Id}).OnDelete(DeleteBehavior.NoAction);
        var record=model.Entity<ProfileRecord>();record.ToTable("profile_record");record.HasKey(x=>new{x.ProfileId,x.RecordId});
        record.HasOne<Profile>().WithMany().HasForeignKey(x=>new{x.AccountId,x.ProfileId}).HasPrincipalKey(x=>new{x.AccountId,x.Id}).OnDelete(DeleteBehavior.Cascade);
        record.HasOne<VaultRecord>().WithMany().HasForeignKey(x=>new{x.AccountId,x.RecordId}).HasPrincipalKey(x=>new{x.AccountId,x.Id}).OnDelete(DeleteBehavior.Cascade);
        var folder=model.Entity<ProfileFolder>();folder.ToTable("profile_folder");folder.HasKey(x=>new{x.ProfileId,x.FolderId});
        folder.HasOne<Profile>().WithMany().HasForeignKey(x=>new{x.AccountId,x.ProfileId}).HasPrincipalKey(x=>new{x.AccountId,x.Id}).OnDelete(DeleteBehavior.Cascade);
        folder.HasOne<VaultFolder>().WithMany().HasForeignKey(x=>new{x.AccountId,x.FolderId}).HasPrincipalKey(x=>new{x.AccountId,x.Id}).OnDelete(DeleteBehavior.Cascade);
        var collection=model.Entity<ProfileCollection>();collection.ToTable("profile_collection");collection.HasKey(x=>new{x.ProfileId,x.CollectionId});
        collection.HasOne<Profile>().WithMany().HasForeignKey(x=>x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        collection.HasOne<VaultCollection>().WithMany().HasForeignKey(x=>x.CollectionId).OnDelete(DeleteBehavior.Cascade);
        var grant=model.Entity<CollectionGrant>();grant.ToTable("collection_grant");grant.HasIndex(x=>x.PublicId).IsUnique();
        grant.HasIndex(x=>new{x.CollectionId,x.RecipientAccountId}).IsUnique();grant.Property(x=>x.RecipientKeyEnvelope).IsRequired();grant.Property(x=>x.Revision).IsConcurrencyToken();
        grant.HasOne<VaultCollection>().WithMany().HasForeignKey(x=>x.CollectionId).OnDelete(DeleteBehavior.Cascade);
        grant.HasOne<Account>().WithMany().HasForeignKey(x=>x.RecipientAccountId).OnDelete(DeleteBehavior.Cascade);
    }
    private static void ConfigureResource(ModelBuilder model,Type type,string table)
    {
        var entity=model.Entity(type);entity.ToTable(table);entity.HasIndex("PublicId").IsUnique();entity.HasIndex("AccountId","ServerSequence");
        entity.HasAlternateKey("AccountId","Id");entity.Property<byte[]>("Envelope").IsRequired();entity.Property<long>("Revision").IsConcurrencyToken();
        entity.Property<long>("ServerSequence").HasDefaultValueSql("nextval('cerberus.server_sequence')");
        entity.HasOne(typeof(Account)).WithMany().HasForeignKey("AccountId").OnDelete(DeleteBehavior.Cascade);
    }
}
