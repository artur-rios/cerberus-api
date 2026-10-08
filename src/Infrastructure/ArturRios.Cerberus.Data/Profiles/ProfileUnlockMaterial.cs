using System.Text.Json;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Profiles;

internal static class ProfileUnlockMaterial
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false};
    internal static async Task<ProfileAccessMaterial> ReadAsync(AppDbContext db,long accountId,Guid accountPublicId,Guid actor,Profile profile,CancellationToken ct)
    {
        var protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==accountId,ct);
        if(!CollectionGrantBinding.TryReadPins(protection,out var pins))throw new JsonException("Invalid current protection.");
        var envelope=JsonSerializer.Deserialize<EncryptedEnvelope>(profile.Envelope,Json);
        var wrappers=JsonSerializer.Deserialize<ProfileKeyWrappers>(profile.KeyWrappers,Json);
        var material=new ProfileAccessMaterial(accountPublicId,profile.PublicId,profile.Revision,envelope!,wrappers!);
        if(!material.IsValid(actor,profile.PublicId,profile.Revision) || profile.EditedAt.Ticks<TimeSpan.TicksPerMicrosecond
            || profile.EditedAt.Offset!=TimeSpan.Zero || profile.EditedAt.Ticks%TimeSpan.TicksPerMicrosecond!=0
            || !wrappers!.IsBound(accountPublicId,profile.PublicId,envelope!.KeyEpoch,wrappers.MasterKeyWrapper.GrantRevision,actor,pins!)
            || !await ProfileVerifierIsolation.IsUniqueAsync(db,accountId,wrappers.UnlockVerifier,profile.Id,ct))throw new JsonException("Invalid scoped protection.");
        return material;
    }
}
