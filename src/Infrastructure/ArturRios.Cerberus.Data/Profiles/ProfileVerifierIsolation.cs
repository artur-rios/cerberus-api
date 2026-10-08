using System.Text.Json;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Profiles;

internal static class ProfileVerifierIsolation
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false};
    // The caller holds the owning account lock through check and admission.
    internal static async Task<bool> IsUniqueAsync(AppDbContext db,long accountId,PublicJwk candidate,long? excludedProfileId,CancellationToken ct)
    {
        if(candidate?.IsValid()!=true)throw new JsonException("Invalid scoped verifier.");
        var fingerprint=candidate.Fingerprint();
        var retained=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==accountId && (excludedProfileId==null || x.Id!=excludedProfileId))
            .Select(x=>x.KeyWrappers).ToArrayAsync(ct);
        var unique=true;
        foreach(var bytes in retained)
        {
            var wrappers=JsonSerializer.Deserialize<ProfileKeyWrappers>(bytes,Json);
            if(wrappers?.UnlockVerifier?.IsValid()!=true)throw new JsonException("Invalid retained scoped verifier.");
            if(wrappers.UnlockVerifier.Fingerprint()==fingerprint)unique=false;
        }
        return unique;
    }
}
