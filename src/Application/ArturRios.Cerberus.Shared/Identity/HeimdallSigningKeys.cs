using System.Text;
using ArturRios.Cerberus.Shared.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Cerberus.Shared.Identity;

internal static class HeimdallSigningKeys
{
    internal static IEnumerable<SecurityKey> Read(CerberusOptions options)
    {
        yield return new SymmetricSecurityKey(Encoding.ASCII.GetBytes(options.AuthValidationSecret!));
        if(options.AuthValidationSecretPrevious is not null)
            yield return new SymmetricSecurityKey(Encoding.ASCII.GetBytes(options.AuthValidationSecretPrevious));
    }
}
