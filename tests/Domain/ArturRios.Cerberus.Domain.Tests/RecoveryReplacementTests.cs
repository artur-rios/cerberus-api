using System.Text.Json;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;

namespace ArturRios.Cerberus.Domain.Tests;

public class RecoveryReplacementTests
{
    [UnitFact]
    public void GivenFreshDistinctRecoveryReplacement_WhenApplying_ThenRetainOtherPinsAndReplaceCompleteWrappers()
    {
        using var current=new ProtectionFixture();using var next=new ProtectionFixture();var r=Replacement(current,next);
        Assert.True(r.IsValid());Assert.True(r.IsValidTransition(current.Material));var m=r.Replace(current.Material);
        Assert.True(m.IsValid());Assert.Equal(current.Material.UnlockVerifier,m.UnlockVerifier);Assert.Equal(current.Material.RecipientKey,m.RecipientKey);Assert.Equal(current.Material.AuthorKey,m.AuthorKey);
        Assert.Equal(next.Material.RecoveryVerifier,m.RecoveryVerifier);Assert.Equal(r.PasswordWrapper,m.PasswordWrapper);Assert.Equal(r.RecoveryWrapper,m.RecoveryWrapper);
        Assert.Equal(2,m.PasswordWrapper.KeyEpoch);Assert.Equal(2,m.RecoveryWrapper.Generation);
    }

    [UnitTheory]
    [InlineData("operation")][InlineData("idempotency")][InlineData("revision")][InlineData("unsafeRevision")][InlineData("password")]
    [InlineData("recovery")][InlineData("verifier")][InlineData("offCurve")][InlineData("fingerprint")][InlineData("differentEpochs")]
    public void GivenMalformedVisibleRecoveryInput_WhenValidating_ThenReject(string invalid)
    {
        using var current=new ProtectionFixture();using var next=new ProtectionFixture();var r=Replacement(current,next);
        r=invalid switch
        {
            "operation"=>r with { Operation="unsupported" },"idempotency"=>r with { IdempotencyKey=Guid.Empty },
            "revision"=>r with { ExpectedRevision=0 },"unsafeRevision"=>r with { ExpectedRevision=9007199254740992 },
            "password"=>r with { PasswordWrapper=null! },"recovery"=>r with { RecoveryWrapper=null! },"verifier"=>r with { NewRecoveryVerifier=null! },
            "offCurve"=>r with { NewRecoveryVerifier=r.NewRecoveryVerifier with { X=ProtectionFixture.Encode(new byte[32]),Y=ProtectionFixture.Encode(new byte[32]) } },
            "fingerprint"=>r with { RecoveryWrapper=r.RecoveryWrapper with { ProofKeyFingerprint=ProtectionFixture.Encode(new byte[32]) } },
            _=>r with { RecoveryWrapper=r.RecoveryWrapper with { KeyEpoch=3 } }
        };
        Assert.False(r.IsValid());
    }

    [UnitTheory]
    [InlineData("oldRecovery")][InlineData("unlock")][InlineData("recipient")][InlineData("author")][InlineData("slot")][InlineData("generation")]
    [InlineData("slotOverflow")][InlineData("generationOverflow")][InlineData("passwordSalt")][InlineData("passwordNonce")]
    [InlineData("kdfSalt")][InlineData("recoverySalt")][InlineData("recoveryNonce")]
    public void GivenSubstitutedRoleStaleEpochOrReusedContext_WhenRecovering_ThenRejectTransition(string invalid)
    {
        using var current=new ProtectionFixture();using var next=new ProtectionFixture();var r=Replacement(current,next);var old=current.Material;
        PublicJwk? key=invalid switch { "oldRecovery"=>old.RecoveryVerifier,"unlock"=>old.UnlockVerifier,"recipient"=>old.RecipientKey,"author"=>old.AuthorKey,_=>null };
        if(key is not null)r=r with { NewRecoveryVerifier=key,RecoveryWrapper=r.RecoveryWrapper with { ProofKeyFingerprint=key.Fingerprint() } };
        if(invalid=="slot")r=r with { PasswordWrapper=r.PasswordWrapper with { KeyEpoch=3 },RecoveryWrapper=r.RecoveryWrapper with { KeyEpoch=3 } };
        if(invalid=="generation")r=r with { RecoveryWrapper=r.RecoveryWrapper with { Generation=3 } };
        if(invalid=="slotOverflow")old=old with { PasswordWrapper=old.PasswordWrapper with { KeyEpoch=9007199254740991 },RecoveryWrapper=old.RecoveryWrapper with { KeyEpoch=9007199254740991 } };
        if(invalid=="generationOverflow")old=old with { RecoveryWrapper=old.RecoveryWrapper with { Generation=9007199254740991 } };
        if(invalid=="passwordSalt")r=r with { PasswordWrapper=r.PasswordWrapper with { KeySalt=old.PasswordWrapper.KeySalt } };
        if(invalid=="passwordNonce")r=r with { PasswordWrapper=r.PasswordWrapper with { Nonce=old.PasswordWrapper.Nonce } };
        if(invalid=="kdfSalt")r=r with { PasswordWrapper=r.PasswordWrapper with { Kdf=r.PasswordWrapper.Kdf with { Salt=old.PasswordWrapper.Kdf.Salt } } };
        if(invalid=="recoverySalt")r=r with { RecoveryWrapper=r.RecoveryWrapper with { KeySalt=old.RecoveryWrapper.KeySalt } };
        if(invalid=="recoveryNonce")r=r with { RecoveryWrapper=r.RecoveryWrapper with { Nonce=old.RecoveryWrapper.Nonce } };
        Assert.False(r.IsValidTransition(old));
    }

    [UnitTheory]
    [InlineData("unknown")][InlineData("private")][InlineData("numericString")]
    public void GivenSecretBearingOrNoncanonicalRecoveryBody_WhenParsing_ThenReject(string invalid)
    {
        using var current=new ProtectionFixture();using var next=new ProtectionFixture();var raw=JsonSerializer.Serialize(Replacement(current,next),ProtectionFixture.Json);
        raw=invalid switch { "unknown"=>raw.Insert(1,"\"recoverySecret\":\"secret\","),"private"=>raw.Replace("\"crv\":","\"d\":\"private\",\"crv\":"),_=>raw.Replace("\"expectedRevision\":1","\"expectedRevision\":\"1\"") };
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<RecoveryReplacement>(raw,ProtectionFixture.Json));
    }
    [UnitFact]
    public void GivenNativeFullWrapperRefresh_WhenApplying_ThenAdvanceRecoveryAndRetainOtherPins()
    {
        using var current=new ProtectionFixture();using var next=new ProtectionFixture();var r=Replacement(current,next) with { Operation="refresh-recovery" };
        Assert.True(r.IsValid());Assert.True(r.IsValidTransition(current.Material));var m=r.Replace(current.Material);
        Assert.Equal(current.Material.UnlockVerifier,m.UnlockVerifier);Assert.Equal(current.Material.RecipientKey,m.RecipientKey);Assert.Equal(current.Material.AuthorKey,m.AuthorKey);Assert.Equal(next.Material.RecoveryVerifier,m.RecoveryVerifier);
        Assert.Equal(2,m.PasswordWrapper.KeyEpoch);Assert.Equal(2,m.RecoveryWrapper.Generation);
    }
    [UnitFact]
    public void GivenRefreshPreservingOldPasswordWrapper_WhenValidating_ThenRejectUnreviewedMixedSlotTransition()
    {
        using var current=new ProtectionFixture();using var next=new ProtectionFixture();var r=Replacement(current,next) with { Operation="refresh-recovery",PasswordWrapper=current.Material.PasswordWrapper };
        Assert.False(r.IsValidTransition(current.Material));
    }
    private static RecoveryReplacement Replacement(ProtectionFixture current,ProtectionFixture next)
    {
        var fresh=current.Rewrap();return new("recover",Guid.NewGuid(),1,fresh.PasswordWrapper,
            fresh.RecoveryWrapper with { Generation=2,ProofKeyFingerprint=next.Material.RecoveryVerifier.Fingerprint() },next.Material.RecoveryVerifier);
    }
}
