using ArturRios.Cerberus.Data.Resources;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Profiles;

internal sealed record ResolvedProfileAssociations(long[] Records,long[] Folders,long[] Collections,string? Error=null);
internal static class ProfileAssociationResolver
{
    internal static async Task<ResolvedProfileAssociations> ResolveAsync(AppDbContext db,long account,Guid actor,Guid[] records,Guid[] folders,Guid[] collections,CancellationToken ct)
    {
        // Foreign owner operations lock collection then grant. Never lock the foreign account/profile.
        var locked=collections.Length==0?[]:await db.Collections.FromSqlInterpolated($"""
            SELECT c.* FROM cerberus.collection c WHERE c.public_id=ANY({collections}) ORDER BY c.public_id FOR SHARE
            """).AsNoTracking().ToListAsync(ct);
        var foreign=locked.Where(x=>x.AccountId!=account).Select(x=>x.Id).ToArray();
        var grants=foreign.Length==0?[]:await db.CollectionGrants.FromSqlInterpolated($"""
            SELECT g.* FROM cerberus.collection_grant g WHERE g.collection_id=ANY({foreign}) AND g.recipient_account_id={account}
            ORDER BY g.public_id FOR SHARE
            """).AsNoTracking().ToListAsync(ct);
        // Fresh queries after SHARE waits observe a committed revocation or trash transition.
        var rs=await ProfileAssociationVisibility.Records(db,account).Where(x=>records.Contains(x.PublicId)).Select(x=>x.Id).ToArrayAsync(ct);
        var fs=await ProfileAssociationVisibility.Folders(db,account).Where(x=>folders.Contains(x.PublicId)).Select(x=>x.Id).ToArrayAsync(ct);
        var cs=await ProfileAssociationVisibility.Collections(db,account).Where(x=>collections.Contains(x.PublicId)).Select(x=>x.Id).ToArrayAsync(ct);
        if(rs.Length!=records.Length || fs.Length!=folders.Length || cs.Length!=collections.Length)return new([],[],[],"not_found");
        if(foreign.Length>0)
        {
            var owners=locked.Where(x=>x.AccountId!=account).Select(x=>x.AccountId).Distinct().ToArray();
            var identities=await db.Accounts.AsNoTracking().Where(x=>owners.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,ct);
            var pinIds=owners.Append(account).ToArray();
            var pins=await db.VaultProtections.AsNoTracking().Where(x=>pinIds.Contains(x.AccountId)).ToDictionaryAsync(x=>x.AccountId,ct);
            if(!CollectionGrantBinding.TryReadPins(pins.GetValueOrDefault(account),out var recipient))return new([],[],[],"persistence_unavailable");
            foreach(var collection in locked.Where(x=>x.AccountId!=account))
            {
                var grant=grants.SingleOrDefault(x=>x.CollectionId==collection.Id);
                if(grant is null)return new([],[],[],"not_found");
                if(!identities.TryGetValue(collection.AccountId,out var owner)
                    || !CollectionGrantBinding.TryReadPins(pins.GetValueOrDefault(collection.AccountId),out var author)
                    || !CollectionGrantBinding.IsBound(grant,collection,owner.PublicId,actor,author!,recipient!))return new([],[],[],"persistence_unavailable");
            }
        }
        return new(rs,fs,cs);
    }
}
