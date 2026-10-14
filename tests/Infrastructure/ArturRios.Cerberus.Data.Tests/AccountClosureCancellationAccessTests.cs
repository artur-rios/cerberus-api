using System.Security.Cryptography;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountClosureCancellationStoreTests
{
    [FunctionalFact]
    public async Task GivenRetainedProtectionAndRevokedSharing_WhenCancelling_ThenRequireNewUnlockAndNeverReviveGrants()
    {
        using var key=new ProtectionFixture();using var recipientKey=new ProtectionFixture();var s=await Setup(key);
        var recipient=await ProfileSetup.Create(fixture,recipientKey);var items=await AssociationSetup.Items(fixture,s);
        var grant=await AssociationSetup.Grant(fixture,s,key,recipient,recipientKey,items.Collection);
        await using var db=fixture.CreateContext();await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES({s.InternalId},{items.Collection.Id},{items.Record.Id})");
        await Close(s);Assert.Null((await new AccountClosureCancellationStore(fixture).CancelAsync(Request(s),default)).Error);
        Assert.NotNull((await new RecordReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,items.Record.PublicId),default)).Error);
        var store=new VaultProtectionStore(fixture);var raw="{}"u8.ToArray();
        var challenge=await store.ChallengeAsync(s.Actor,ProtocolBinary.Encode(SHA256.HashData(raw)),default);Assert.Null(challenge.Error);
        var unlocked=await store.UnlockAsync(s.Actor,challenge.Data!.ProtectionRevision,challenge.Data.ChallengeId,key.Sign(challenge.Data),raw,default);
        Assert.Null(unlocked.Error);Assert.True(OpaqueAccessHandle.TryHash(unlocked.Data!.Access,out var verifier));
        var read=await new RecordReadStore(fixture).ReadAsync(new(s.Actor,verifier,items.Record.PublicId),default);Assert.Null(read.Error);Assert.Equal(items.Record.Envelope,read.Data!.Record.Envelope);
        var oldGrant=await db.CollectionGrants.AsNoTracking().SingleAsync(x=>x.Id==grant.Id);Assert.Equal(CollectionGrantState.Revoked,oldGrant.State);Assert.Equal(2,oldGrant.Revision);Assert.Empty(oldGrant.RecipientKeyEnvelope);
        Assert.NotNull((await new RecordReadStore(fixture).ReadAsync(new(recipient.Actor,recipient.Verifier,items.Record.PublicId),default)).Error);
        Assert.Equal(2,await db.Accounts.Where(x=>x.Id==recipient.InternalId).Select(x=>x.RevocationGeneration).SingleAsync());
        Assert.True(await db.VaultAccessSessions.Where(x=>x.AccountId==s.InternalId&&x.HandleVerifier==s.Verifier).Select(x=>x.Revoked).SingleAsync());
    }
    [FunctionalFact]
    public async Task GivenCancelledClosure_WhenExplicitlyClosingAgain_ThenResetSameWorkForNewWindowAndKeepOldAccessRevoked()
    {
        using var key=new ProtectionFixture();var s=await Setup(key);var first=await Close(s);
        await using var db=fixture.CreateContext();var job=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="account-purge/"+s.AccountId);
        Assert.Null((await new AccountClosureCancellationStore(fixture).CancelAsync(Request(s),default)).Error);
        var second=await new AccountClosureStore(fixture).RequestAsync(new(s.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),3),default);Assert.Null(second.Error);
        Assert.Equal(4,second.Details!.Revision);Assert.Equal(4,second.Details.RevocationGeneration);Assert.True(second.Details.ClosureDeadline>first.ClosureDeadline);
        var work=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.Id==job.Id);Assert.Equal(job.PublicId,work.PublicId);Assert.Equal(second.Details.ClosureDeadline,work.DueAt);
        Assert.Null(work.CompletedAt);Assert.Null(work.ClaimToken);Assert.Null(work.ClaimExpiresAt);Assert.Equal(0,work.Attempts);
        Assert.False(await db.VaultAccessSessions.AnyAsync(x=>x.AccountId==s.InternalId&&!x.Revoked));
    }
    [FunctionalFact]
    public async Task GivenOutstandingPurgeClaim_WhenCancellingBeforeDeadline_ThenClearClaimAndFenceStaleWorkerCompletion()
    {
        using var key=new ProtectionFixture();var s=await Setup(key);await Close(s);await using var db=fixture.CreateContext();var token=Guid.NewGuid();
        await db.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+s.AccountId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.ClaimToken,token).SetProperty(w=>w.ClaimExpiresAt,DateTimeOffset.UtcNow.AddMinutes(1)).SetProperty(w=>w.Attempts,1));
        var result=await new AccountClosureCancellationStore(fixture).CancelAsync(Request(s),default);Assert.Null(result.Error);
        var work=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="account-purge/"+s.AccountId);Assert.Null(work.ClaimToken);Assert.Null(work.ClaimExpiresAt);Assert.Equal(1,work.Attempts);
        Assert.NotNull(work.CompletedAt);
        var before=await Snapshot(db,s);
        Assert.False(await new ArturRios.Cerberus.Data.Operations.RetentionWorkStore(fixture).TryCompleteAsync(
            new(work.PublicId,token,work.OperationKey,DateTimeOffset.UtcNow.AddMinutes(1)),DateTimeOffset.UtcNow,default));
        Assert.Equal(before,await Snapshot(db,s));
    }
}
