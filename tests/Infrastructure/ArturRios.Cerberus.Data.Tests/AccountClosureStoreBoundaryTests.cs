using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountClosureStoreTests
{
    [FunctionalTheory]
    [InlineData("revisionZero","persistence_unavailable")][InlineData("generationZero","persistence_unavailable")]
    [InlineData("policyZero","persistence_unavailable")][InlineData("revisionMax","revision_conflict")]
    [InlineData("generationMax","revision_conflict")][InlineData("terminal","not_found")]
    [InlineData("activeTimes","persistence_unavailable")][InlineData("pendingNoTime","persistence_unavailable")]
    [InlineData("activeWork","persistence_unavailable")]
    public async Task GivenInvalidPersistedClosureState_WhenClosing_ThenRetainTheCapturedState(string kind,string error)
    {
        using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,key);await using var db=fixture.CreateContext();
        var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);
        if(kind=="revisionZero")a.Revision=0;if(kind=="generationZero")a.RevocationGeneration=0;if(kind=="policyZero")a.PolicyRevision=0;
        if(kind=="revisionMax")a.Revision=ProtocolBinary.MaxInteger;if(kind=="generationMax")a.RevocationGeneration=ProtocolBinary.MaxInteger;
        if(kind=="activeTimes")a.ClosureRequestedAt=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if(kind=="pendingNoTime")a.State=AccountState.ClosurePending;
        if(kind=="terminal")db.TerminalErasures.Add(new(){ResourceKind="account",ResourceId=a.PublicId,DeletedAt=DateTimeOffset.UtcNow});
        if(kind=="activeWork")db.RetentionWorkItems.Add(new(){OperationKey="account-purge/"+a.PublicId,DueAt=DateTimeOffset.UtcNow.AddDays(30)});
        await db.SaveChangesAsync();var before=await AccountSnapshot(db,a.Id);
        var result=await new AccountClosureStore(fixture).RequestAsync(Request(s) with {ExpectedRevision=a.Revision>0?a.Revision:1},default);
        Assert.Equal(error,result.Error);Assert.Null(result.Details);Assert.Equal(before,await AccountSnapshot(db,a.Id));
        Assert.True(await db.VaultAccessSessions.AnyAsync(x=>x.AccountId==s.InternalId&&!x.Revoked));
    }
    [FunctionalTheory][InlineData("deadline")][InlineData("missingWork")][InlineData("workDeadline")][InlineData("completedWork")]
    public async Task GivenCorruptPendingClosureMetadata_WhenReplaying_ThenRejectWithoutExtendingDeadline(string kind)
    {
        using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,key);var store=new AccountClosureStore(fixture);
        var first=await store.RequestAsync(Request(s),default);Assert.Null(first.Error);await using var db=fixture.CreateContext();
        if(kind=="deadline")await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET closure_purge_at=closure_purge_at+interval '1 second' WHERE id={s.InternalId}");
        if(kind=="missingWork")await db.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+s.AccountId).ExecuteDeleteAsync();
        if(kind=="workDeadline")await db.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+s.AccountId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.DueAt,DateTimeOffset.UtcNow));
        if(kind=="completedWork")await db.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+s.AccountId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.CompletedAt,DateTimeOffset.UtcNow));
        var before=await AccountSnapshot(db,s.InternalId);var result=await store.RequestAsync(Request(s),default);
        Assert.Equal("persistence_unavailable",result.Error);Assert.Equal(before,await AccountSnapshot(db,s.InternalId));
    }
    [FunctionalFact]
    public async Task GivenCompletedPriorWorkAndPendingUnlock_WhenClosing_ThenResetJobAndConsumeChallengeWithoutRemovingProtection()
    {
        using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,key);await using var db=fixture.CreateContext();
        var work=new RetentionWorkItem {OperationKey="account-purge/"+s.AccountId,DueAt=DateTimeOffset.UtcNow.AddDays(-1),CompletedAt=DateTimeOffset.UtcNow,Attempts=3,ClaimToken=Guid.NewGuid(),ClaimExpiresAt=DateTimeOffset.UtcNow};
        db.RetentionWorkItems.Add(work);var challenge=new VaultUnlockChallenge {AccountId=s.InternalId,PublicId=Guid.NewGuid(),Challenge=[1],ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(1)};
        db.VaultUnlockChallenges.Add(challenge);await db.SaveChangesAsync();
        var result=await new AccountClosureStore(fixture).RequestAsync(Request(s),default);Assert.Null(result.Error);
        var saved=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.Id==work.Id);Assert.Equal(work.PublicId,saved.PublicId);
        Assert.Equal(0,saved.Attempts);Assert.Null(saved.ClaimToken);Assert.Null(saved.ClaimExpiresAt);Assert.Null(saved.CompletedAt);
        Assert.Equal(result.Details!.ClosureDeadline,saved.DueAt);Assert.True(await db.VaultUnlockChallenges.Where(x=>x.Id==challenge.Id).Select(x=>x.Consumed).SingleAsync());
        Assert.True(await db.VaultProtections.AnyAsync(x=>x.AccountId==s.InternalId));
    }

    [FunctionalFact]
    public async Task GivenSeveralGrantsForOneRecipient_WhenClosing_ThenAdvanceItsGenerationOnceAndDenyOldNativeAccess()
    {
        using var key=new ProtectionFixture();using var recipientKey=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,key);
        var recipient=await ProfileSetup.Create(fixture,recipientKey);var a=await AssociationSetup.Items(fixture,s);var b=await AssociationSetup.Items(fixture,s);
        await AssociationSetup.Grant(fixture,s,key,recipient,recipientKey,a.Collection);await AssociationSetup.Grant(fixture,s,key,recipient,recipientKey,b.Collection);
        Assert.Null((await new AccountClosureStore(fixture).RequestAsync(Request(s),default)).Error);
        await using var db=fixture.CreateContext();Assert.Equal(2,await db.Accounts.Where(x=>x.Id==recipient.InternalId).Select(x=>x.RevocationGeneration).SingleAsync());
        var read=await new ArturRios.Cerberus.Data.Records.RecordReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,a.Record.PublicId),default);
        Assert.NotNull(read.Error);Assert.Null(read.Data);
        var account=await new AccountReadStore(fixture).ReadAsync(s.Actor,s.Verifier,DateTimeOffset.UtcNow,default);Assert.NotNull(account.Error);Assert.Null(account.Account);
        var challenge=await new ArturRios.Cerberus.Data.Protection.VaultProtectionStore(fixture).ChallengeAsync(s.Actor,
            ProtocolBinary.Encode(System.Security.Cryptography.SHA256.HashData("{}"u8.ToArray())),default);
        Assert.NotNull(challenge.Error);Assert.Null(challenge.Data);
        var renewal=await new ArturRios.Cerberus.Data.Offline.OfflineAuthorizationStore(fixture,new ClosureSigner()).RenewAsync(new(s.Actor,s.Verifier,Guid.NewGuid(),1),default);
        Assert.NotNull(renewal.Error);Assert.Null(renewal.Details);
    }
    private sealed class ClosureSigner : ArturRios.Cerberus.Domain.Offline.IOfflineLeaseSigner
    {
        public ArturRios.Cerberus.Domain.Offline.SignedOfflineLease? Sign(ArturRios.Cerberus.Domain.Offline.OfflineLeaseSnapshot snapshot)
            => throw new InvalidOperationException("closed accounts must never reach lease signing");
    }
    private static Task<string> AccountSnapshot(AppDbContext db,long id)=>db.Database.SqlQuery<string>($"SELECT to_jsonb(a)::text AS \"Value\" FROM cerberus.account a WHERE a.id={id}").SingleAsync();
}
