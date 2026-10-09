using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Profiles;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ProfileKeyWrappers(string UnlockMode, PublicJwk UnlockVerifier, RecipientEnvelope MasterKeyWrapper, PasswordWrapper? PasswordWrapper)
{
    public bool IsValid() => UnlockVerifier?.IsValid() == true && MasterKeyWrapper?.IsValid() == true
        && (UnlockMode == "Master" ? PasswordWrapper is null : UnlockMode == "PerProfile" && PasswordWrapper?.IsValid() == true);
    public bool IsBound(Guid owner, Guid profile, long epoch, long revision, Guid identity, ProtectionMaterial pins) =>
        IsValid() && pins?.IsValid() == true
        && new[] {pins.UnlockVerifier,pins.RecoveryVerifier,pins.RecipientKey,pins.AuthorKey}.All(x=>x.Fingerprint()!=UnlockVerifier.Fingerprint())
        && MasterKeyWrapper.Verify(owner,"profile",profile,epoch,profile,revision,identity,pins.RecipientKey,pins.AuthorKey);
    public bool IsValidRotation(ProfileKeyWrappers current) => IsValid() && current?.IsValid() == true
        && UnlockMode == current.UnlockMode && UnlockVerifier == current.UnlockVerifier
        && MasterKeyWrapper.Enc != current.MasterKeyWrapper.Enc && MasterKeyWrapper.Ciphertext != current.MasterKeyWrapper.Ciphertext
        && (UnlockMode == "Master" || current.PasswordWrapper!.KeyEpoch < ProtocolBinary.MaxInteger
            && PasswordWrapper!.KeyEpoch == current.PasswordWrapper.KeyEpoch + 1
            && PasswordWrapper.KeySalt != current.PasswordWrapper.KeySalt && PasswordWrapper.Nonce != current.PasswordWrapper.Nonce
            && PasswordWrapper.Kdf.Salt != current.PasswordWrapper.Kdf.Salt);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ProfileCreateInput(Guid ProfileId, EncryptedEnvelope Envelope, ProfileKeyWrappers KeyWrappers,
    DateTimeOffset EditedAt, Guid[] RecordIds, Guid[] FolderIds, Guid[] CollectionIds)
{
    public bool IsValid() => ProfileId != Guid.Empty && Envelope?.IsValid() == true && Envelope.KeyEpoch == 1
        && KeyWrappers?.IsValid() == true && KeyWrappers.MasterKeyWrapper.KeyEpoch == 1 && KeyWrappers.MasterKeyWrapper.GrantRevision == 1
        && (KeyWrappers.PasswordWrapper is null || KeyWrappers.PasswordWrapper.KeyEpoch == 1)
        && EditedAt.Ticks >= TimeSpan.TicksPerMicrosecond && EditedAt.Offset == TimeSpan.Zero
        && ValidIds(RecordIds) && ValidIds(FolderIds) && ValidIds(CollectionIds);
    private static bool ValidIds(Guid[]? ids) => ids is not null && ids.All(x=>x!=Guid.Empty) && ids.Distinct().Count()==ids.Length;
}
public sealed record ProfileCreateRequest(Guid Actor, string AccessVerifier, ProfileCreateInput Input);
public sealed record ProfileCreateDetails(Guid ProfileId, long Revision, long ServerSequence, DateTimeOffset EditedAt);
public interface IProfileCreateStore
{
    Task<VaultResult<ProfileCreateDetails>> CreateAsync(ProfileCreateRequest request, CancellationToken cancellationToken);
}
