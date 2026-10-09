using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Profiles;

internal static class ProfileProjectionQuery
{
    internal static IQueryable<ProfileListRow> Select(AppDbContext db,IQueryable<Profile> profiles)
    {
        var records=ProfileAssociationVisibility.Records(db);
        var folders=ProfileAssociationVisibility.Folders(db);
        var collections=ProfileAssociationVisibility.Collections(db);
        var grants=db.CollectionGrants.Where(g=>g.State==CollectionGrantState.Active
            && (g.Access==CollectionGrantAccess.ReadOnly || g.Access==CollectionGrantAccess.ReadWrite)
            && g.Revision>0 && g.Revision<=ProtocolBinary.MaxInteger && !db.TerminalErasures.Any(e=>(e.ResourceKind == "grant" && e.ResourceId == g.PublicId)));
        // Three correlated scalar aggregates avoid multiplying the independent link sets.
        return profiles.Select(p=>new ProfileListRow(p.PublicId,p.Revision,p.ServerSequence,p.EditedAt,p.Envelope,p.KeyWrappers)
        {
            RecordIds=(from link in db.ProfileRecords join r in records on link.RecordId equals r.Id
                where link.ProfileId==p.Id && r.AccountId==p.AccountId group r by link.ProfileId into g
                select EF.Functions.ArrayAgg(g.Select(r=>r.PublicId).OrderBy(id=>id))).FirstOrDefault() ?? Array.Empty<Guid>(),
            FolderIds=(from link in db.ProfileFolders join f in folders on link.FolderId equals f.Id
                where link.ProfileId==p.Id && f.AccountId==p.AccountId group f by link.ProfileId into g
                select EF.Functions.ArrayAgg(g.Select(f=>f.PublicId).OrderBy(id=>id))).FirstOrDefault() ?? Array.Empty<Guid>(),
            CollectionIds=(from link in db.ProfileCollections join c in collections on link.CollectionId equals c.Id
                where link.ProfileId==p.Id && (c.AccountId==p.AccountId || grants.Any(g=>g.CollectionId==c.Id && g.RecipientAccountId==p.AccountId))
                group c by link.ProfileId into g
                select EF.Functions.ArrayAgg(g.Select(c=>c.PublicId).OrderBy(id=>id))).FirstOrDefault() ?? Array.Empty<Guid>()
        });
    }
}
