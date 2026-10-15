using System.Security.Cryptography;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.TestSupport;

namespace ArturRios.Cerberus.Domain.Tests;

public class SoftwareSecretContractTests
{
    [UnitTheory][InlineData("record",false)][InlineData("collection",false)][InlineData("record",true)]
    public void GivenExactProvisionedTargetOrCollectionMember_WhenValidatingDisclosure_ThenRequireMatchingNativeResourceBinding(string kind,bool member)
    {
        var c=Create(kind,member);Assert.True(c.Input.IsValid());Assert.True(c.Details.IsValid(c.Identity,c.Input));
    }
    [UnitTheory][InlineData("kind")][InlineData("grant")][InlineData("target")][InlineData("emptySelection")][InlineData("collectionSelection")]
    public void GivenInvalidVisibleSoftwareSecretSelection_WhenValidating_ThenReject(string kind)
    {
        var input=new SoftwareSecretInput(Guid.NewGuid(),"record",Guid.NewGuid());
        input=kind switch{"kind"=>input with{Kind="folder"},"grant"=>input with{GrantId=Guid.Empty},"target"=>input with{Id=Guid.Empty},"emptySelection"=>input with{CollectionId=Guid.Empty},_=>input with{Kind="collection",CollectionId=Guid.NewGuid()}};
        Assert.False(input.IsValid());
    }
    [UnitTheory]
    [InlineData("owner")][InlineData("grant")][InlineData("grantRevision")][InlineData("unsafeRevision")]
    [InlineData("targetId")][InlineData("targetKind")][InlineData("targetRevision")][InlineData("epoch")][InlineData("format")]
    [InlineData("provisionId")][InlineData("provisionKind")][InlineData("directCiphertext")]
    [InlineData("wrapGrant")][InlineData("wrapRevision")][InlineData("wrapIdentity")][InlineData("wrapEpoch")][InlineData("signature")]
    [InlineData("author")][InlineData("recipient")][InlineData("actor")]
    public void GivenMismatchedDisclosureOrProvisioning_WhenValidating_ThenRejectWithoutPartialData(string kind)
    {
        var c=Create("record",false);var d=c.Details;
        using var other=ECDsa.Create(ECCurve.NamedCurves.nistP256);var key=ProtectionFixture.Public(other);
        d=kind switch
        {
            "owner"=>d with{OwnerAccountId=Guid.Empty},"grant"=>d with{GrantId=Guid.NewGuid()},"grantRevision"=>d with{GrantRevision=0},"unsafeRevision"=>d with{GrantRevision=9007199254740992},
            "targetId"=>d with{Target=d.Target with{Id=Guid.NewGuid()}},"targetKind"=>d with{Target=d.Target with{Kind="collection"}},"targetRevision"=>d with{Target=d.Target with{Revision=0}},
            "epoch"=>d with{Target=d.Target with{KeyEpoch=2}},"format"=>d with{Target=d.Target with{Envelope=d.Target.Envelope with{Format="unknown"}}},
            "provisionId"=>d with{Provisioning=d.Provisioning with{Resource=d.Provisioning.Resource with{Id=Guid.NewGuid()}}},
            "provisionKind"=>d with{Provisioning=d.Provisioning with{Resource=d.Provisioning.Resource with{Kind="collection"}}},
            "directCiphertext"=>d with{Target=d.Target with{Envelope=d.Target.Envelope with{Ciphertext="BAUG"}}},
            "wrapGrant"=>d with{Provisioning=d.Provisioning with{KeyEnvelope=d.Provisioning.KeyEnvelope with{GrantId=Guid.NewGuid()}}},
            "wrapRevision"=>d with{Provisioning=d.Provisioning with{KeyEnvelope=d.Provisioning.KeyEnvelope with{GrantRevision=2}}},
            "wrapIdentity"=>d with{Provisioning=d.Provisioning with{KeyEnvelope=d.Provisioning.KeyEnvelope with{RecipientIdentityId=Guid.NewGuid()}}},
            "wrapEpoch"=>d with{Provisioning=d.Provisioning with{KeyEnvelope=d.Provisioning.KeyEnvelope with{KeyEpoch=2}}},
            "signature"=>d with{Provisioning=d.Provisioning with{KeyEnvelope=d.Provisioning.KeyEnvelope with{Signature=ProtectionFixture.Encode(new byte[64])}}},
            "author"=>d with{AuthorKey=key},"recipient"=>d with{RecipientKey=new(key.Crv,key.Kty,key.X,key.Y)},_=>d
        };
        Assert.False(d.IsValid(kind=="actor"?c.Identity with{ApplicationId=Guid.NewGuid()}:c.Identity,c.Input));
    }
    [UnitFact]
    public void GivenDifferentCollectionSelection_WhenValidatingMemberRecord_ThenNeverUseAnAlternateProvisioningPath()
    {
        var c=Create("record",true);Assert.False(c.Details.IsValid(c.Identity,c.Input with{CollectionId=Guid.NewGuid()}));
        Assert.False(c.Details.IsValid(c.Identity,c.Input with{CollectionId=null}));
    }
    private static SoftwareSecretFixture.Case Create(string kind,bool member)=>SoftwareSecretFixture.Create(kind,member);
}
