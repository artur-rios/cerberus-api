using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.TestSupport;

namespace ArturRios.Cerberus.Domain.Tests;

public class SyncResourceTests
{
    private static readonly Guid Actor = Guid.NewGuid(), Profile = Guid.NewGuid();
    private static readonly DateTimeOffset Edited = DateTimeOffset.FromUnixTimeSeconds(1000);
    private static EncryptedEnvelope Envelope() => new("cerberus-content-v1", 1, ProtectionFixture.Encode(new byte[32]), ProtectionFixture.Encode(new byte[12]), "AQID", ProtectionFixture.Encode(new byte[16]));
    [UnitTheory]
    [InlineData("profile")][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("grant")]
    public void GivenNativeScopedResource_WhenCreatingUpsert_ThenValidateTypedContent(string kind)
    {
        using var client = new ProtectionFixture();
        var data = Data(kind, client);
        Assert.True(data.IsValid(Actor, Profile));
        Assert.True(new SyncChangeItem(1, kind, data.ResourceId, "upsert", data).IsValid(Actor, Profile));
        Assert.False(new SyncChangeItem(1, kind, Guid.NewGuid(), "upsert", data).IsValid(Actor, Profile));
    }
    [UnitTheory]
    [InlineData("removed")][InlineData("permanently-deleted")]
    public void GivenKnownDisclosedIdentifier_WhenRemoving_ThenRequireMinimalTypedMetadata(string type)
    {
        var item = new SyncChangeItem(1, "record", Guid.NewGuid(), type, DeletedAt: type == "permanently-deleted" ? Edited : null);
        Assert.True(item.IsValid(Actor, Profile));
        Assert.Null(item.Data);
        Assert.False((item with { ResourceId = Guid.Empty }).IsValid(Actor, Profile));
        Assert.False((item with { ResourceKind = "account" }).IsValid(Actor, Profile));
    }
    [UnitTheory]
    [InlineData("revision")][InlineData("sequence")][InlineData("unsafe")][InlineData("time")]
    [InlineData("utc")][InlineData("precision")][InlineData("envelope")][InlineData("identifier")]
    public void GivenInvalidEncryptedMetadata_WhenValidating_ThenReject(string invalid)
    {
        var r = new SyncRecordData(Guid.NewGuid(), 1, 1, Edited, Envelope());
        r = invalid switch
        {
            "revision" => r with { Revision = 0 }, "sequence" => r with { ServerSequence = 0 },
            "unsafe" => r with { ServerSequence = 9007199254740992 }, "time" => r with { EditedAt = default },
            "utc" => r with { EditedAt = Edited.ToOffset(TimeSpan.FromHours(1)) },
            "precision" => r with { EditedAt = Edited.AddTicks(1) }, "envelope" => r with { Envelope = Envelope() with { Format = "unknown" } },
            _ => r with { ResourceId = Guid.Empty }
        };
        Assert.False(r.IsValid(Actor, Profile));
    }
    [UnitFact]
    public void GivenDifferentProfileOrRecipientBinding_WhenValidating_ThenReject()
    {
        using var client = new ProtectionFixture();
        Assert.False(Data("profile", client).IsValid(Actor, Guid.NewGuid()));
        Assert.False(Data("profile", client).IsValid(Guid.NewGuid(), Profile));
        Assert.False(Data("grant", client).IsValid(Guid.NewGuid(), Profile));
        var c = (SyncCollectionData)Data("collection", client);
        Assert.False((c with { KeyEpoch = 2 }).IsValid(Actor, Profile));
    }
    private static SyncResourceData Data(string kind, ProtectionFixture client) => kind switch
    {
        "profile" => new SyncProfileData(Profile, 1, 1, Edited, Envelope(), new ProfileKeyWrappers("Master", client.Material.UnlockVerifier, client.Wrap(Guid.NewGuid(), "profile", Profile, Actor), null), [], [], []),
        "record" => new SyncRecordData(Guid.NewGuid(), 1, 1, Edited, Envelope()),
        "folder" => new SyncFolderData(Guid.NewGuid(), 1, 1, Edited, Envelope()),
        "collection" => new SyncCollectionData(Guid.NewGuid(), 1, 1, Edited, Envelope(), 1),
        _ => Grant(client)
    };
    private static SyncGrantData Grant(ProtectionFixture client)
    { var id = Guid.NewGuid(); return new(id, 1, Guid.NewGuid(), client.Wrap(Guid.NewGuid(), "collection", id, Actor)); }
}
