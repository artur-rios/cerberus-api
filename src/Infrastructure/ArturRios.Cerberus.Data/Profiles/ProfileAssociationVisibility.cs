using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
namespace ArturRios.Cerberus.Data.Profiles;

internal static class ProfileAssociationVisibility
{
    internal static IQueryable<VaultRecord> Records(AppDbContext db,long account)=>db.Records.Where(x=>x.AccountId==account && x.DeletedAt==null
        && !db.TerminalErasures.Any(e=>e.ResourceId==x.PublicId)
        && db.Accounts.Any(a=>a.Id==account && a.State==AccountState.Active && !db.TerminalErasures.Any(e=>e.ResourceId==a.PublicId)));
    internal static IQueryable<VaultFolder> Folders(AppDbContext db,long account)=>db.Folders.Where(x=>x.AccountId==account && x.DeletedAt==null
        && !db.TerminalErasures.Any(e=>e.ResourceId==x.PublicId)
        && db.Accounts.Any(a=>a.Id==account && a.State==AccountState.Active && !db.TerminalErasures.Any(e=>e.ResourceId==a.PublicId)));
    internal static IQueryable<VaultCollection> Collections(AppDbContext db,long account)=>db.Collections.Where(x=>x.DeletedAt==null
        && !db.TerminalErasures.Any(e=>e.ResourceId==x.PublicId)
        && db.Accounts.Any(a=>a.Id==x.AccountId && a.State==AccountState.Active && !db.TerminalErasures.Any(e=>e.ResourceId==a.PublicId))
        && (x.AccountId==account || db.CollectionGrants.Any(g=>g.CollectionId==x.Id && g.RecipientAccountId==account
            && g.State==CollectionGrantState.Active && (g.Access==CollectionGrantAccess.ReadOnly || g.Access==CollectionGrantAccess.ReadWrite)
            && g.Revision>0 && g.Revision<=ProtocolBinary.MaxInteger && !db.TerminalErasures.Any(e=>e.ResourceId==g.PublicId))));
}
