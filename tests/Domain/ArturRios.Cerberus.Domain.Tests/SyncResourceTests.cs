using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.TestSupport;

namespace ArturRios.Cerberus.Domain.Tests;

public class SyncResourceTests
{
    private static readonly Guid s_actor = Guid.NewGuid(), s_profile = Guid.NewGuid();
    private static readonly DateTimeOffset s_edited = DateTimeOffset.FromUnixTimeSeconds(1000);
    private static EncryptedEnvelope Envelope() => new("cerberus-content-v1", 1, ProtectionFixture.Encode(new byte[32]), ProtectionFixture.Encode(new byte[12]), "AQID", ProtectionFixture.Encode(new byte[16]));
    [UnitTheory]
    [InlineData("profile")]
    [InlineData("record")]
    [InlineData("folder")]
    [InlineData("collection")]
    [InlineData("grant")]
    public void GivenNativeScopedResource_WhenCreatingUpsert_ThenValidateTypedContent(string kind)
    {
        using var client = new ProtectionFixture();
        var data = Data(kind, client);
        Assert.True(data.IsValid(s_actor, s_profile));
        Assert.True(new SyncChangeItem(1, kind, data.ResourceId, "upsert", data).IsValid(s_actor, s_profile));
        Assert.False(new SyncChangeItem(1, kind, Guid.NewGuid(), "upsert", data).IsValid(s_actor, s_profile));
    }
    [UnitTheory]
    [InlineData("removed")]
    [InlineData("permanently-deleted")]
    public void GivenKnownDisclosedIdentifier_WhenRemoving_ThenRequireMinimalTypedMetadata(string type)
    {
        var item = new SyncChangeItem(1, "record", Guid.NewGuid(), type, DeletedAt: type == "permanently-deleted" ? s_edited : null);
        Assert.True(item.IsValid(s_actor, s_profile));
        Assert.Null(item.Data);
        Assert.False((item with { ResourceId = Guid.Empty }).IsValid(s_actor, s_profile));
        Assert.False((item with { ResourceKind = "account" }).IsValid(s_actor, s_profile));
    }
    [UnitTheory]
    [InlineData("revision")]
    [InlineData("sequence")]
    [InlineData("unsafe")]
    [InlineData("time")]
    [InlineData("utc")]
    [InlineData("precision")]
    [InlineData("envelope")]
    [InlineData("identifier")]
    public void GivenInvalidEncryptedMetadata_WhenValidating_ThenReject(string invalid)
    {
        var r = new SyncRecordData(Guid.NewGuid(), 1, 1, s_edited, Envelope());
        r = invalid switch
        {
            "revision" => r with { Revision = 0 },
            "sequence" => r with { ServerSequence = 0 },
            "unsafe" => r with { ServerSequence = 9007199254740992 },
            "time" => r with { EditedAt = default },
            "utc" => r with { EditedAt = s_edited.ToOffset(TimeSpan.FromHours(1)) },
            "precision" => r with { EditedAt = s_edited.AddTicks(1) },
            "envelope" => r with { Envelope = Envelope() with { Format = "unknown" } },
            _ => r with { ResourceId = Guid.Empty }
        };
        Assert.False(r.IsValid(s_actor, s_profile));
    }
    [UnitFact]
    public void GivenDifferentProfileOrRecipientBinding_WhenValidating_ThenReject()
    {
        using var client = new ProtectionFixture();
        Assert.False(Data("profile", client).IsValid(s_actor, Guid.NewGuid()));
        Assert.False(Data("profile", client).IsValid(Guid.NewGuid(), s_profile));
        Assert.False(Data("grant", client).IsValid(Guid.NewGuid(), s_profile));
        var c = (SyncCollectionData)Data("collection", client);
        Assert.False((c with { KeyEpoch = 2 }).IsValid(s_actor, s_profile));
    }
    private static SyncResourceData Data(string kind, ProtectionFixture client) => kind switch
    {
        "profile" => new SyncProfileData(s_profile, 1, 1, s_edited, Envelope(), new ProfileKeyWrappers("Master", client.Material.UnlockVerifier, client.Wrap(Guid.NewGuid(), "profile", s_profile, s_actor), null), [], [], []),
        "record" => new SyncRecordData(Guid.NewGuid(), 1, 1, s_edited, Envelope()),
        "folder" => new SyncFolderData(Guid.NewGuid(), 1, 1, s_edited, Envelope()),
        "collection" => new SyncCollectionData(Guid.NewGuid(), 1, 1, s_edited, Envelope(), 1),
        _ => Grant(client)
    };
    private static SyncGrantData Grant(ProtectionFixture client)
    { var id = Guid.NewGuid(); return new(id, 1, Guid.NewGuid(), client.Wrap(Guid.NewGuid(), "collection", id, s_actor)); }
}
