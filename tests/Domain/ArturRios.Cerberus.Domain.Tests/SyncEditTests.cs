using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.TestSupport;

namespace ArturRios.Cerberus.Domain.Tests;

public class SyncEditTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.FromUnixTimeSeconds(1000);
    private static EncryptedEnvelope Envelope() => new("cerberus-content-v1", 1, ProtectionFixture.Encode(new byte[32]), ProtectionFixture.Encode(new byte[12]), "AQID", ProtectionFixture.Encode(new byte[16]));
    [UnitTheory]
    [InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public void GivenValidOfflineUpdate_WhenValidating_ThenBindOperationAndTypedNativeContent(string kind)
    {
        var input = new SyncContentEditInput(Guid.NewGuid(), Envelope(), Time);
        SyncEditInput edit = kind switch
        {
            "record" => new SyncRecordUpdate(Guid.NewGuid(), input), "folder" => new SyncFolderUpdate(Guid.NewGuid(), input),
            "collection" => new SyncCollectionUpdate(Guid.NewGuid(), input), _ => new SyncProfileUpdate(Guid.NewGuid(), input)
        };
        Assert.True(edit.IsValid()); Assert.Equal(kind, edit.ResourceKind); Assert.Equal("update", edit.Action);
        Assert.Equal(input.ResourceId, edit.ResourceId); Assert.Equal(Time, edit.EditedAt);
        Assert.True(new SyncEditsInput([edit]).IsValid(100));
        StrictVariant(edit);
    }
    [UnitTheory]
    [InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public void GivenValidClientIdentifiers_WhenCreatingOfflineResource_ThenReuseNativeCreationRules(string kind)
    {
        using var keys = new ProtectionFixture(); var id = Guid.NewGuid(); var actor = Guid.NewGuid();
        SyncEditInput edit = kind switch
        {
            "record" => new SyncRecordCreate(Guid.NewGuid(), new RecordCreateInput(id, Envelope(), Time, [])),
            "folder" => new SyncFolderCreate(Guid.NewGuid(), new FolderCreateInput(id, Envelope(), Time, [])),
            "collection" => new SyncCollectionCreate(Guid.NewGuid(), new CollectionCreateInput(id, Envelope(), Time, [], [], [])),
            _ => new SyncProfileCreate(Guid.NewGuid(), new ProfileCreateInput(id, Envelope(), new("Master", keys.Material.UnlockVerifier, keys.Wrap(Guid.NewGuid(), "profile", id, actor), null), Time, [], [], []))
        };
        Assert.True(edit.IsValid()); Assert.Equal(kind, edit.ResourceKind); Assert.Equal("create", edit.Action); Assert.Equal(id, edit.ResourceId);
        StrictVariant(edit);
    }
    private static void StrictVariant(SyncEditInput edit)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize<SyncEditInput>(edit, options);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["OfflineLatestEdit"] = true;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SyncEditInput>(node.ToJsonString(), options));
        node.AsObject().Remove("OfflineLatestEdit"); node.AsObject().Remove("input");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SyncEditInput>(node.ToJsonString(), options));
    }
    [UnitTheory]
    [InlineData("operation")][InlineData("identifier")][InlineData("format")][InlineData("utc")][InlineData("time")]
    public void GivenInvalidVisibleEdit_WhenValidating_ThenReject(string invalid)
    {
        var input = new SyncContentEditInput(invalid == "identifier" ? Guid.Empty : Guid.NewGuid(),
            invalid == "format" ? Envelope() with { Format = "unknown" } : Envelope(),
            invalid == "utc" ? Time.ToOffset(TimeSpan.FromHours(1)) : invalid == "time" ? default : Time);
        var edit = new SyncRecordUpdate(invalid == "operation" ? Guid.Empty : Guid.NewGuid(), input);
        Assert.False(edit.IsValid());
    }
    [UnitFact]
    public void GivenEmptyOversizedOrRepeatedOperations_WhenValidatingBatch_ThenRejectBeforeAnyWrite()
    {
        var edit = new SyncRecordUpdate(Guid.NewGuid(), new(Guid.NewGuid(), Envelope(), Time));
        Assert.False(new SyncEditsInput([]).IsValid(100)); Assert.False(new SyncEditsInput([edit, edit]).IsValid(100));
        Assert.False(new SyncEditsInput([edit]).IsValid(0)); Assert.False(new SyncEditsInput([edit, edit with { OperationId = Guid.NewGuid() }]).IsValid(1));
    }
    [UnitTheory]
    [InlineData(-1, false)][InlineData(0, true)][InlineData(1, true)]
    public void GivenClientTimestamp_WhenComparingUnderCurrentResourceLock_ThenApplyLatestIncludingDeterministicTies(int offset, bool apply)
    {
        Assert.Equal(apply, OfflineEditOrder.ShouldApply(Time.AddSeconds(offset), Time));
        Assert.Equal(Time, OfflineEditOrder.Normalize(Time.AddTicks(9)));
    }
    [UnitFact]
    public void GivenPolymorphicEdit_WhenRoundtripping_ThenPreserveExactPayloadAndRejectUnknownOrMissingFields()
    {
        var edit = new SyncRecordUpdate(Guid.NewGuid(), new(Guid.NewGuid(), Envelope(), Time));
        var json = JsonSerializer.Serialize(new SyncEditsInput([edit]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var parsed = JsonSerializer.Deserialize<SyncEditsInput>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(edit, Assert.IsType<SyncRecordUpdate>(Assert.Single(parsed!.Edits)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SyncEditsInput>(json.Replace("record-update", "unknown"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SyncEditsInput>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    [UnitTheory]
    [InlineData("applied", true)][InlineData("superseded", true)][InlineData("denied", false)][InlineData("permanently-deleted", false)]
    public void GivenPerItemOutcome_WhenValidating_ThenExposeWinningMetadataOnlyForVisibleResults(string status, bool winning)
    {
        var result = new SyncEditOutcome(Guid.NewGuid(), "record", Guid.NewGuid(), status, winning ? 1 : null, winning ? 2 : null, winning ? Time : null);
        Assert.True(result.IsValid()); Assert.False((result with { Status = "unknown" }).IsValid());
        Assert.False((result with { Revision = winning ? null : 1 }).IsValid());
    }
}
