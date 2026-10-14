using ArturRios.Cerberus.Data.Sync;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public partial class SyncEditsStoreTests(PostgresFixture fixture)
{
    private static readonly CerberusOptions Options = new() { MaxPageSize = 100 };
    [FunctionalFact]
    public async Task GivenAllEightNativeVariants_WhenUploadingAndReplaying_ThenCommitOneAtomicBatchWithStableSequences()
    {
        using var keys = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys);
        var time = DateTimeOffset.FromUnixTimeSeconds(1000); var record = Create();
        var folder = new SyncFolderCreate(Guid.NewGuid(), new FolderCreateInput(Guid.NewGuid(), ProfileSetup.Envelope(), time, []));
        var collection = new SyncCollectionCreate(Guid.NewGuid(), new CollectionCreateInput(Guid.NewGuid(), ProfileSetup.Envelope(), time, [], [record.ResourceId], [folder.ResourceId]));
        var profile = new SyncProfileCreate(Guid.NewGuid(), ProfileSetup.Input(s, keys, scoped) with { EditedAt = time });
        SyncEditInput[] edits = [record, folder, collection, profile,
            new SyncRecordUpdate(Guid.NewGuid(), new(record.ResourceId, ProfileSetup.Envelope(), time)),
            new SyncFolderUpdate(Guid.NewGuid(), new(folder.ResourceId, ProfileSetup.Envelope(), time)),
            new SyncCollectionUpdate(Guid.NewGuid(), new(collection.ResourceId, ProfileSetup.Envelope(), time)),
            new SyncProfileUpdate(Guid.NewGuid(), new(profile.ResourceId, ProfileSetup.Envelope(), time))];
        var store = new SyncEditsStore(fixture, Options); var request = new SyncEditsRequest(s.Actor, s.Verifier, new(edits));
        var first = await store.UploadAsync(request, default); Assert.Null(first.Error); Assert.Equal(8, first.Details!.Outcomes.Count);
        Assert.All(first.Details.Outcomes, o => Assert.Equal("applied", o.Status));
        var sequences = first.Details.Outcomes.Select(o => o.ServerSequence).ToArray(); Assert.Equal(sequences.Order(), sequences); Assert.Equal(8, sequences.Distinct().Count());
        var replay = await store.UploadAsync(request, default); Assert.Null(replay.Error); Assert.Equal(first.Details.Outcomes, replay.Details!.Outcomes);
        await using var db = fixture.CreateContext(); Assert.Equal(8, await db.SyncOperations.CountAsync(x => x.AccountId == s.InternalId));
        Assert.Equal(2, await db.Profiles.Where(x => x.PublicId == profile.ResourceId).Select(x => x.Revision).SingleAsync());
    }
    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenSharedRecord_WhenUploadingAndReplayingAfterRevocation_ThenRequireCurrentWritePermissionWithoutDisclosingWinningMetadata(bool write)
    {
        using var keys = new ProtectionFixture(); using var recipientKey = new ProtectionFixture();
        var owner = await ProfileSetup.Create(fixture, keys); var recipient = await ProfileSetup.Create(fixture, recipientKey); var items = await AssociationSetup.Items(fixture, owner);
        await using var db = fixture.CreateContext(); db.CollectionRecords.Add(new() { AccountId = owner.InternalId, CollectionId = items.Collection.Id, RecordId = items.Record.Id }); await db.SaveChangesAsync();
        var grant = await AssociationSetup.Grant(fixture, owner, keys, recipient, recipientKey, items.Collection, write ? CollectionGrantAccess.ReadWrite : CollectionGrantAccess.ReadOnly);
        var edit = new SyncRecordUpdate(Guid.NewGuid(), new(items.Record.PublicId, ProfileSetup.Envelope(), DateTimeOffset.UtcNow.AddHours(1)));
        var store = new SyncEditsStore(fixture, Options); var request = new SyncEditsRequest(recipient.Actor, recipient.Verifier, new([edit]));
        var first = await store.UploadAsync(request, default); Assert.Null(first.Error); Assert.Equal(write ? "applied" : "denied", Assert.Single(first.Details!.Outcomes).Status);
        await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked));
        var replay = await store.UploadAsync(request, default); Assert.Null(replay.Error); var denied = Assert.Single(replay.Details!.Outcomes);
        Assert.Equal("denied", denied.Status); Assert.Null(denied.Revision); Assert.Null(denied.ServerSequence); Assert.Null(denied.EditedAt);
        Assert.Equal(write ? 2 : 1, await db.Records.Where(x => x.Id == items.Record.Id).Select(x => x.Revision).SingleAsync());
    }
    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenSelectedProfile_WhenUploading_ThenCreateOnlyInsideCurrentSelectionAndBindReplayScope(bool associated)
    {
        using var keys = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys); var profile = await AssociationSetup.Profile(fixture, s, keys);
        await using var db = fixture.CreateContext(); var profileId = await db.Profiles.Where(x => x.Id == profile).Select(x => x.PublicId).SingleAsync();
        await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ProfileId, profile));
        var record = Create(); if (associated) record = record with { Input = record.Input with { ProfileIds = [profileId] } };
        var request = new SyncEditsRequest(s.Actor, s.Verifier, new([record])); var store = new SyncEditsStore(fixture, Options);
        var result = await store.UploadAsync(request, default); Assert.Null(result.Error); Assert.Equal(profileId, result.Details!.ProfileId);
        Assert.Equal(associated ? "applied" : "denied", Assert.Single(result.Details.Outcomes).Status);
        await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ProfileId, (long?)null));
        Assert.Equal("operation_conflict", (await store.UploadAsync(request, default)).Error);
    }
    [FunctionalFact]
    public async Task GivenEncryptedCreate_WhenUploadingAndReplaying_ThenAtomicallyCommitOneResourceAndStableMinimalOperation()
    {
        using var keys = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys); var edit = Create();
        var request = new SyncEditsRequest(s.Actor, s.Verifier, new([edit])); var store = new SyncEditsStore(fixture, Options);
        var first = await store.UploadAsync(request, default); Assert.Null(first.Error); var item = Assert.Single(first.Details!.Outcomes);
        Assert.Equal("applied", item.Status); Assert.Equal(1, item.Revision); Assert.Equal(edit.ResourceId, item.ResourceId);
        var repeat = await store.UploadAsync(request, default); Assert.Null(repeat.Error); Assert.Equal(item, Assert.Single(repeat.Details!.Outcomes));
        await using var db = fixture.CreateContext(); Assert.Equal(1, await db.Records.CountAsync(x => x.PublicId == edit.ResourceId));
        var operation = await db.SyncOperations.SingleAsync(x => x.AccountId == s.InternalId && x.OperationId == edit.OperationId);
        Assert.Equal(32, operation.RequestFingerprint.Length); Assert.Equal(item.ServerSequence, operation.ServerSequence);
        var columns = db.Model.FindEntityType(typeof(SyncOperation))!.GetProperties().Select(x => x.Name);
        Assert.DoesNotContain(columns, x => x.Contains("Envelope", StringComparison.Ordinal) || x.Contains("Ciphertext", StringComparison.Ordinal));
    }
    [FunctionalFact]
    public async Task GivenChangedOperationReuseAfterAnotherItem_WhenUploading_ThenConflictAndRollbackEarlierBatchWrites()
    {
        using var keys = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys); var store = new SyncEditsStore(fixture, Options); var edit = Create();
        Assert.Null((await store.UploadAsync(new(s.Actor, s.Verifier, new([edit])), default)).Error);
        var earlier = Create(); var changed = edit with { Input = edit.Input with { EditedAt = edit.EditedAt.AddSeconds(1) } };
        var result = await store.UploadAsync(new(s.Actor, s.Verifier, new([earlier, changed])), default);
        Assert.Equal("operation_conflict", result.Error); Assert.Null(result.Details);
        await using var db = fixture.CreateContext(); Assert.False(await db.Records.AnyAsync(x => x.PublicId == earlier.ResourceId));
        Assert.False(await db.SyncOperations.AnyAsync(x => x.AccountId == s.InternalId && x.OperationId == earlier.OperationId));
    }
    [FunctionalFact]
    public async Task GivenOlderThenEqualAndNewerEdits_WhenUploading_ThenPersistLatestTimestampAndDeterministicSequence()
    {
        using var keys = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys); var store = new SyncEditsStore(fixture, Options); var edit = Create();
        var initial = await store.UploadAsync(new(s.Actor, s.Verifier, new([edit])), default); Assert.Null(initial.Error);
        var previous = Assert.Single(initial.Details!.Outcomes);
        foreach (var offset in new[] { -1, 0, 1 })
        {
            var update = new SyncRecordUpdate(Guid.NewGuid(), new(edit.ResourceId, ProfileSetup.Envelope(), edit.EditedAt.AddSeconds(offset)));
            var result = await store.UploadAsync(new(s.Actor, s.Verifier, new([update])), default); Assert.Null(result.Error); var item = Assert.Single(result.Details!.Outcomes);
            Assert.Equal(offset < 0 ? "superseded" : "applied", item.Status);
            if (offset >= 0) { Assert.True(item.ServerSequence > previous.ServerSequence); Assert.Equal(previous.Revision + 1, item.Revision); }
            else Assert.Equal(previous.ServerSequence, item.ServerSequence);
            previous = item;
        }
    }
    [FunctionalFact]
    public async Task GivenUnknownHiddenTargetAndValidCreate_WhenUploading_ThenReportDeniedWithoutBlockingAuthorizedItem()
    {
        using var keys = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys); var store = new SyncEditsStore(fixture, Options);
        var denied = new SyncRecordUpdate(Guid.NewGuid(), new(Guid.NewGuid(), ProfileSetup.Envelope(), DateTimeOffset.FromUnixTimeSeconds(1000))); var create = Create();
        var result = await store.UploadAsync(new(s.Actor, s.Verifier, new([denied, create])), default); Assert.Null(result.Error);
        Assert.Equal("denied", result.Details!.Outcomes[0].Status); Assert.Null(result.Details.Outcomes[0].Revision);
        Assert.Equal("applied", result.Details.Outcomes[1].Status);
    }
    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenTerminalIdentifier_WhenUploading_ThenNeverResurrectAndDisclosePermanentStatusOnlyWithKnownOperationProof(bool known)
    {
        using var keys = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys); var edit = Create(); var store = new SyncEditsStore(fixture, Options);
        if (known) Assert.Null((await store.UploadAsync(new(s.Actor, s.Verifier, new([edit])), default)).Error);
        await using var db = fixture.CreateContext();
        db.TerminalErasures.Add(new() { ResourceKind = "record", ResourceId = edit.ResourceId, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
        await db.Records.Where(x => x.PublicId == edit.ResourceId).ExecuteDeleteAsync();
        var update = new SyncRecordUpdate(Guid.NewGuid(), new(edit.ResourceId, ProfileSetup.Envelope(), edit.EditedAt.AddSeconds(1)));
        var result = await store.UploadAsync(new(s.Actor, s.Verifier, new([update])), default); Assert.Null(result.Error);
        var item = Assert.Single(result.Details!.Outcomes); Assert.Equal(known ? "permanently-deleted" : "denied", item.Status);
        Assert.Null(item.Revision); Assert.False(await db.Records.AnyAsync(x => x.PublicId == edit.ResourceId));
    }
    [FunctionalFact]
    public async Task GivenCorruptRetainedTargetBinding_WhenReplaying_ThenFailClosedWithoutOutcomeOrMutation()
    {
        using var keys = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys); var edit = Create(); var store = new SyncEditsStore(fixture, Options);
        var request = new SyncEditsRequest(s.Actor, s.Verifier, new([edit])); Assert.Null((await store.UploadAsync(request, default)).Error);
        await using var db = fixture.CreateContext(); await db.SyncOperations.Where(x => x.AccountId == s.InternalId && x.OperationId == edit.OperationId)
            .ExecuteUpdateAsync(x => x.SetProperty(o => o.ResourceId, Guid.NewGuid()));
        var result = await store.UploadAsync(request, default); Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Details);
        Assert.Equal(1, await db.Records.Where(x => x.PublicId == edit.ResourceId).Select(x => x.Revision).SingleAsync());
    }
    private static SyncRecordCreate Create() => new(Guid.NewGuid(), new RecordCreateInput(Guid.NewGuid(), ProfileSetup.Envelope(), DateTimeOffset.FromUnixTimeSeconds(1000), []));
}
