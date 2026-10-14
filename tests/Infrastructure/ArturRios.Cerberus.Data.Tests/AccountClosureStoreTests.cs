using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public partial class AccountClosureStoreTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenFreshOwnerWithEncryptedData_WhenClosingThenReplaying_ThenRevokeSessionsRetainDataAndPreserveOriginalDeadline()
    {
        using var key = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, key); var items = await AssociationSetup.Items(fixture, s);
        var profile = await AssociationSetup.Profile(fixture, s, key); var store = new AccountClosureStore(fixture);
        var result = await store.RequestAsync(Request(s), default); Assert.Null(result.Error); var closed = result.Details!;
        Assert.False(closed.AlreadyPending); Assert.True(closed.IsValid()); Assert.Equal(s.AccountId, closed.AccountId);
        Assert.Equal(2, closed.Revision); Assert.Equal(2, closed.RevocationGeneration); Assert.Equal(TimeSpan.FromDays(30), closed.ClosureDeadline - closed.ClosureRequestedAt);
        var replay = await store.RequestAsync(Request(s), default); Assert.Null(replay.Error); Assert.True(replay.Details!.AlreadyPending);
        Assert.Equal(closed.ClosureRequestedAt, replay.Details.ClosureRequestedAt); Assert.Equal(closed.ClosureDeadline, replay.Details.ClosureDeadline);
        await using var db = fixture.CreateContext(); var account = await db.Accounts.SingleAsync(x => x.Id == s.InternalId);
        Assert.Equal(AccountState.ClosurePending, account.State); Assert.Equal(s.AccountEnvelope, account.DetailsEnvelope);
        Assert.Equal(items.Record.Envelope, await db.Records.Where(x => x.Id == items.Record.Id).Select(x => x.Envelope).SingleAsync());
        Assert.Equal(items.Folder.Envelope, await db.Folders.Where(x => x.Id == items.Folder.Id).Select(x => x.Envelope).SingleAsync());
        Assert.True(await db.Profiles.AnyAsync(x => x.Id == profile)); Assert.True(await db.VaultProtections.AnyAsync(x => x.AccountId == s.InternalId));
        Assert.False(await db.VaultAccessSessions.AnyAsync(x => x.AccountId == s.InternalId && !x.Revoked));
        var job = await db.RetentionWorkItems.SingleAsync(x => x.OperationKey == "account-purge/" + s.AccountId);
        Assert.Equal(closed.ClosureDeadline, job.DueAt); Assert.Null(job.CompletedAt);
    }
    [FunctionalFact]
    public async Task GivenIncomingOutgoingAndSoftwareGrants_WhenClosing_ThenEraseProvisioningAndInvalidateOnlyAffectedRecipients()
    {
        using var ownerKey = new ProtectionFixture(); using var otherKey = new ProtectionFixture(); using var recipientKey = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, ownerKey); var other = await ProfileSetup.Create(fixture, otherKey); var recipient = await ProfileSetup.Create(fixture, recipientKey);
        var owned = await AssociationSetup.Items(fixture, s); var foreign = await AssociationSetup.Items(fixture, other);
        var outgoing = await AssociationSetup.Grant(fixture, s, ownerKey, recipient, recipientKey, owned.Collection);
        var incoming = await AssociationSetup.Grant(fixture, other, otherKey, s, ownerKey, foreign.Collection);
        await using var db = fixture.CreateContext(); var software = new SoftwareGrant { PublicId = Guid.NewGuid(), AccountId = s.InternalId,
            HeimdallSoftwarePublicId = Guid.NewGuid(), RecipientKey = [1, 2, 3] };
        db.SoftwareGrants.Add(software); await db.SaveChangesAsync();
        db.SoftwareGrantRecords.Add(new() { AccountId = s.InternalId, GrantId = software.Id, RecordId = owned.Record.Id, RecipientKeyEnvelope = [4, 5] });
        db.SoftwareGrantCollections.Add(new() { AccountId = s.InternalId, GrantId = software.Id, CollectionId = owned.Collection.Id, RecipientKeyEnvelope = [6, 7] }); await db.SaveChangesAsync();
        var result = await new AccountClosureStore(fixture).RequestAsync(Request(s), default); Assert.Null(result.Error);
        foreach (var id in new[] { incoming.Id, outgoing.Id })
        { var grant = await db.CollectionGrants.AsNoTracking().SingleAsync(x => x.Id == id); Assert.Equal(CollectionGrantState.Revoked, grant.State); Assert.Equal(2, grant.Revision); Assert.Empty(grant.RecipientKeyEnvelope); }
        Assert.Equal(2, await db.Accounts.Where(x => x.Id == recipient.InternalId).Select(x => x.RevocationGeneration).SingleAsync());
        Assert.Equal(1, await db.Accounts.Where(x => x.Id == other.InternalId).Select(x => x.RevocationGeneration).SingleAsync());
        var saved = await db.SoftwareGrants.AsNoTracking().SingleAsync(x => x.Id == software.Id); Assert.Equal(SoftwareGrantState.Revoked, saved.State); Assert.Equal(2, saved.Revision); Assert.Empty(saved.RecipientKey);
        Assert.False(await db.SoftwareGrantRecords.AnyAsync(x => x.GrantId == software.Id)); Assert.False(await db.SoftwareGrantCollections.AnyAsync(x => x.GrantId == software.Id));
        Assert.True(await db.Collections.AnyAsync(x => x.Id == foreign.Collection.Id)); Assert.True(await db.Records.AnyAsync(x => x.Id == foreign.Record.Id));
    }
    [FunctionalTheory]
    [InlineData("revision", "revision_conflict")][InlineData("missing", "not_found")][InlineData("old", "fresh_authentication_required")]
    [InlineData("future", "fresh_authentication_required")]
    public async Task GivenInvalidCurrentClosureAuthority_WhenRequesting_ThenLeaveAllStateUnchanged(string invalid, string error)
    {
        using var key = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, key); var r = Request(s);
        r = invalid switch { "revision" => r with { ExpectedRevision = 2 }, "missing" => r with { Actor = Guid.NewGuid() },
            "old" => r with { AuthenticationIssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds() },
            _ => r with { AuthenticationIssuedAt = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds() } };
        var result = await new AccountClosureStore(fixture).RequestAsync(r, default); Assert.Equal(error, result.Error); Assert.Null(result.Details);
        await using var db = fixture.CreateContext(); var account = await db.Accounts.SingleAsync(x => x.Id == s.InternalId);
        Assert.Equal(AccountState.Active, account.State); Assert.Equal(1, account.Revision); Assert.Equal(1, account.RevocationGeneration);
        Assert.Null(account.ClosurePurgeAt); Assert.True(await db.VaultAccessSessions.AnyAsync(x => x.AccountId == s.InternalId && !x.Revoked));
        Assert.False(await db.RetentionWorkItems.AnyAsync(x => x.OperationKey == "account-purge/" + s.AccountId));
    }
    private static AccountClosureRequest Request(ProfileSetup.State s) => new(s.Actor, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1);
}
