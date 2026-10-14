using ArturRios.Cerberus.Command.Sync;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class UploadSyncEditsHandlerTests
{
    private static readonly Guid Actor = Guid.NewGuid(), Account = Guid.NewGuid(), Id = Guid.NewGuid(), Operation = Guid.NewGuid();
    private const string Access = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly DateTimeOffset Time = DateTimeOffset.FromUnixTimeSeconds(1000);
    private static readonly CerberusOptions Options = new() { MaxPageSize = 100 };
    private static SyncEditInput Edit() => new SyncRecordUpdate(Operation, new(Id, new("cerberus-content-v1", 1,
        ProtectionFixture.Encode(new byte[32]), ProtectionFixture.Encode(new byte[12]), "AQID", ProtectionFixture.Encode(new byte[16])), Time));
    private static UploadSyncEditsCommand Command()
    { var command = new UploadSyncEditsCommand { Edits = [Edit()] }; command.SetContext(Actor, Access); return command; }
    [UnitTheory]
    [InlineData("identity", "authentication_required")][InlineData("missing", "vault_access_required")]
    [InlineData("handle", "validation_failed")][InlineData("empty", "validation_failed")][InlineData("duplicate", "validation_failed")]
    public async Task GivenInvalidCurrentContextOrBatch_WhenUploading_ThenRejectBeforePersistence(string kind, string error)
    {
        var command = Command(); command.SetContext(kind == "identity" ? Guid.Empty : Actor, kind == "missing" ? null : kind == "handle" ? "invalid" : Access);
        if (kind == "empty") command.Edits = [];
        if (kind == "duplicate") command.Edits = [Edit(), Edit()];
        var output = await new UploadSyncEditsHandler(new Mock<ISyncEditsStore>(MockBehavior.Strict).Object, Options).HandleAsync(command);
        Assert.Contains(error, output.Errors); Assert.Null(output.Data);
    }
    [UnitTheory]
    [InlineData("not_found")][InlineData("vault_access_denied")][InlineData("operation_conflict")][InlineData("persistence_unavailable")]
    public async Task GivenStorageOrScopeError_WhenUploading_ThenReturnStableFailureWithoutOutcomes(string error)
    {
        var output = await Handler(new(Error: error)).HandleAsync(Command()); Assert.Contains(error, output.Errors); Assert.Null(output.Data);
    }
    [UnitTheory]
    [InlineData("applied")][InlineData("superseded")][InlineData("denied")][InlineData("permanently-deleted")]
    public async Task GivenValidPerItemResult_WhenHandling_ThenPreserveBoundOrderedOutcomes(string status)
    {
        var winning = status is "applied" or "superseded";
        var item = new SyncEditOutcome(Operation, "record", Id, status, winning ? 1 : null, winning ? 2 : null, winning ? Time : null);
        var output = await Handler(new(new(Account, null, Time, [item]))).HandleAsync(Command());
        Assert.True(output.Success); Assert.Equal(item, Assert.Single(output.Data!.Outcomes)); Assert.Equal(Account, output.Data.AccountId);
    }
    [UnitTheory]
    [InlineData("account")][InlineData("count")][InlineData("operation")][InlineData("kind")][InlineData("identifier")]
    [InlineData("metadata")][InlineData("timestamp")][InlineData("unknownError")][InlineData("errorData")]
    public async Task GivenInconsistentStoredResult_WhenHandling_ThenFailClosed(string kind)
    {
        var item = new SyncEditOutcome(Operation, "record", Id, "applied", 1, 2, Time);
        item = kind switch
        {
            "operation" => item with { OperationId = Guid.NewGuid() }, "kind" => item with { ResourceKind = "folder" },
            "identifier" => item with { ResourceId = Guid.NewGuid() }, "metadata" => item with { Status = "denied" },
            "timestamp" => item with { EditedAt = Time.AddSeconds(1) }, _ => item
        };
        var details = new SyncEditsDetails(kind == "account" ? Guid.Empty : Account, null, Time, kind == "count" ? [] : [item]);
        var result = kind == "unknownError" ? new SyncEditsResult(Error: "internal") : new(details, kind == "errorData" ? "vault_access_denied" : null);
        var output = await Handler(result).HandleAsync(Command()); Assert.Contains("persistence_unavailable", output.Errors); Assert.Null(output.Data);
    }
    [UnitFact]
    public async Task GivenCancelledCaller_WhenUploading_ThenPropagateBeforeWriting()
    {
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(new()).HandleAsync(Command(), ct.Token));
    }
    private static UploadSyncEditsHandler Handler(SyncEditsResult result)
    {
        var store = new Mock<ISyncEditsStore>(MockBehavior.Strict);
        store.Setup(x => x.UploadAsync(It.Is<SyncEditsRequest>(r => r.Actor == Actor
            && r.AccessVerifier == "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925"), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return new(store.Object, Options);
    }
}
