using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;

namespace ArturRios.Cerberus.Domain.Tests;

public class ProtectionChangeTests
{
    [UnitTheory]
    [InlineData("rewrap")][InlineData("rotate-content")]
    public void GivenCompleteFreshReplacement_WhenValidating_ThenAcceptSupportedTransition(string mode)
    {
        using var client = new ProtectionFixture(); var change = Valid(client,mode);
        Assert.True(change.IsValid()); Assert.True(change.IsValidTransition(client.Material));
    }

    [UnitTheory]
    [InlineData("account")][InlineData("protectionRevision")][InlineData("accountRevision")][InlineData("unsafeRevision")]
    [InlineData("mode")][InlineData("material")][InlineData("missingSet")][InlineData("extraRewrap")]
    [InlineData("missingContent")][InlineData("duplicateContent")][InlineData("foreignContent")][InlineData("kind")]
    [InlineData("contentRevision")][InlineData("contentEnvelope")]
    public void GivenMalformedReplacementSet_WhenValidating_ThenRejectBeforePersistence(string invalid)
    {
        using var client = new ProtectionFixture();var c = Valid(client,"rotate-content");var item = c.ContentReplacements[0];
        c = invalid switch
        {
            "account" => c with { AccountId = Guid.Empty }, "protectionRevision" => c with { ExpectedProtectionRevision = 0 },
            "accountRevision" => c with { ExpectedAccountRevision = 0 }, "unsafeRevision" => c with { ExpectedProtectionRevision = 9007199254740992 },
            "mode" => c with { Mode = "partial" }, "material" => c with { Material = null! },
            "missingSet" => c with { ContentReplacements = null! }, "extraRewrap" => c with { Mode = "rewrap" },
            "missingContent" => c with { ContentReplacements = [] }, "duplicateContent" => c with { ContentReplacements = [item,item] },
            "foreignContent" => c with { ContentReplacements = [item with { ResourceId = Guid.NewGuid() }] },
            "kind" => c with { ContentReplacements = [item with { ResourceKind = "profile" }] },
            "contentRevision" => c with { ContentReplacements = [item with { ExpectedRevision = 2 }] },
            "contentEnvelope" => c with { ContentReplacements = [item with { Envelope = item.Envelope with { KeyEpoch = 0 } }] }, _ => c
        };
        Assert.False(c.IsValid());
    }

    [UnitTheory]
    [InlineData("unlock")][InlineData("recovery")][InlineData("recipient")][InlineData("author")][InlineData("generation")]
    [InlineData("epoch")][InlineData("overflow")][InlineData("passwordSalt")][InlineData("passwordNonce")]
    [InlineData("kdfSalt")][InlineData("recoverySalt")][InlineData("recoveryNonce")]
    public void GivenKeySubstitutionOrReusedWrapperContext_WhenChanging_ThenRejectTransition(string invalid)
    {
        using var old = new ProtectionFixture(); using var other = new ProtectionFixture();var c=Valid(old,"rewrap");var m=c.Material;
        m=invalid switch
        {
            "unlock" => m with { UnlockVerifier=other.Material.UnlockVerifier },
            "recovery" => m with { RecoveryVerifier=other.Material.RecoveryVerifier,RecoveryWrapper=m.RecoveryWrapper with { ProofKeyFingerprint=other.Material.RecoveryVerifier.Fingerprint() } },
            "recipient" => m with { RecipientKey=other.Material.RecipientKey }, "author" => m with { AuthorKey=other.Material.AuthorKey },
            "generation" => m with { RecoveryWrapper=m.RecoveryWrapper with { Generation=2 } },
            "epoch" => m with { PasswordWrapper=m.PasswordWrapper with { KeyEpoch=3 },RecoveryWrapper=m.RecoveryWrapper with { KeyEpoch=3 } },
            "passwordSalt" => m with { PasswordWrapper=m.PasswordWrapper with { KeySalt=old.Material.PasswordWrapper.KeySalt } },
            "passwordNonce" => m with { PasswordWrapper=m.PasswordWrapper with { Nonce=old.Material.PasswordWrapper.Nonce } },
            "kdfSalt" => m with { PasswordWrapper=m.PasswordWrapper with { Kdf=m.PasswordWrapper.Kdf with { Salt=old.Material.PasswordWrapper.Kdf.Salt } } },
            "recoverySalt" => m with { RecoveryWrapper=m.RecoveryWrapper with { KeySalt=old.Material.RecoveryWrapper.KeySalt } },
            "recoveryNonce" => m with { RecoveryWrapper=m.RecoveryWrapper with { Nonce=old.Material.RecoveryWrapper.Nonce } },_=>m
        };
        var current=invalid=="overflow" ? old.Material with { PasswordWrapper=old.Material.PasswordWrapper with { KeyEpoch=9007199254740991 },RecoveryWrapper=old.Material.RecoveryWrapper with { KeyEpoch=9007199254740991 } } : old.Material;
        Assert.False((c with { Material=m }).IsValidTransition(current));
    }

    [UnitTheory]
    [InlineData("unknown")][InlineData("numericString")][InlineData("privateKey")]
    public void GivenNoncanonicalWireReplacement_WhenParsing_ThenReject(string invalid)
    {
        using var client=new ProtectionFixture();var raw=JsonSerializer.Serialize(Valid(client,"rewrap"),ProtectionFixture.Json);
        raw=invalid switch
        {
            "unknown"=>raw.Insert(1,"\"password\":\"secret\","),
            "numericString"=>raw.Replace("\"expectedProtectionRevision\":1","\"expectedProtectionRevision\":\"1\""),
            _=>raw.Replace("\"crv\":","\"d\":\"private\",\"crv\":")
        };
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<ProtectionChange>(raw,ProtectionFixture.Json));
    }

    private static ProtectionChange Valid(ProtectionFixture client,string mode)
    {
        var id=Guid.NewGuid();var envelope=new EncryptedEnvelope("cerberus-content-v1",2,ProtectionFixture.Encode(new byte[32]),ProtectionFixture.Encode(new byte[12]),"AQID",ProtectionFixture.Encode(new byte[16]));
        return new(id,1,3,mode,client.Rewrap(),mode=="rewrap" ? [] : [new("account",id,3,envelope)]);
    }
}
