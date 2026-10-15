using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public partial class AccountClosureCancellationStoreTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenUnexpiredPendingClosure_WhenCancelling_ThenRestoreActiveAndCompleteWorkWithoutRevivingOldAccess()
    {
        using var key=new ProtectionFixture();var s=await Setup(key);var items=await AssociationSetup.Items(fixture,s);
        await Close(s);var result=await new AccountClosureCancellationStore(fixture).CancelAsync(Request(s),default);Assert.Null(result.Error);
        Assert.True(result.Details!.IsValid());Assert.Equal(s.AccountId,result.Details.AccountId);Assert.Equal(3,result.Details.Revision);Assert.Equal(3,result.Details.RevocationGeneration);
        await using var db=fixture.CreateContext();var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);
        Assert.Equal(AccountState.Active,a.State);Assert.Null(a.ClosureRequestedAt);Assert.Null(a.ClosurePurgeAt);Assert.Equal(s.AccountEnvelope,a.DetailsEnvelope);
        Assert.Equal(items.Record.Envelope,await db.Records.Where(x=>x.Id==items.Record.Id).Select(x=>x.Envelope).SingleAsync());
        Assert.True(await db.VaultProtections.AnyAsync(x=>x.AccountId==s.InternalId));Assert.False(await db.VaultAccessSessions.AnyAsync(x=>x.AccountId==s.InternalId&&!x.Revoked));
        var work=await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="account-purge/"+s.AccountId);Assert.Equal(result.Details.CancelledAt,work.CompletedAt);Assert.Null(work.ClaimToken);Assert.Null(work.ClaimExpiresAt);
        var old=await new AccountReadStore(fixture).ReadAsync(s.Actor,s.Verifier,DateTimeOffset.UtcNow,default);Assert.NotNull(old.Error);Assert.Null(old.Account);
        var replay=await new AccountClosureCancellationStore(fixture).CancelAsync(Request(s),default);Assert.Equal("revision_conflict",replay.Error);
        Assert.Equal(3,await db.Accounts.Where(x=>x.Id==s.InternalId).Select(x=>x.Revision).SingleAsync());
    }
    [FunctionalTheory][InlineData("revision","revision_conflict")][InlineData("expired","closure_expired")]
    [InlineData("terminal","not_found")][InlineData("missing","not_found")][InlineData("old","fresh_authentication_required")]
    [InlineData("future","fresh_authentication_required")]
    public async Task GivenInvalidCurrentCancellationAuthority_WhenCancelling_ThenKeepClosureAndPurgeWork(string kind,string error)
    {
        using var key=new ProtectionFixture();var s=await Setup(key);await Close(s);await using var db=fixture.CreateContext();
        if(kind=="expired")await Expire(db,s);
        if(kind=="terminal"){db.TerminalErasures.Add(new(){ResourceKind="account",ResourceId=s.AccountId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var before=await Snapshot(db,s);var request=Request(s);request=kind switch {"revision"=>request with{ExpectedRevision=1},
            "missing"=>request with{Actor=Guid.NewGuid()},"old"=>request with{AuthenticationIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-60},
            "future"=>request with{AuthenticationIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3600},_=>request};
        var result=await new AccountClosureCancellationStore(fixture).CancelAsync(request,default);Assert.Equal(error,result.Error);Assert.Null(result.Details);Assert.Equal(before,await Snapshot(db,s));
    }
    [FunctionalTheory][InlineData("pending",null)][InlineData("active","authentication_required")]
    [InlineData("expired","closure_expired")][InlineData("terminal","authentication_required")]
    public async Task GivenFreshIdentity_WhenReadingRestrictedRecovery_ThenReturnOnlyUnexpiredPendingMetadata(string kind,string? error)
    {
        using var key=new ProtectionFixture();var s=await Setup(key);if(kind!="active")await Close(s);await using var db=fixture.CreateContext();
        if(kind=="expired")await Expire(db,s);
        if(kind=="terminal"){db.TerminalErasures.Add(new(){ResourceKind="account",ResourceId=s.AccountId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var before=await Snapshot(db,s);var result=await new AccountClosureRecoveryStore(fixture).ReadAsync(new(s.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),default);
        Assert.Equal(error,result.Error);if(error is null){Assert.True(result.Details!.IsValid());Assert.Equal(s.AccountId,result.Details.AccountId);Assert.Equal(2,result.Details.Revision);}else Assert.Null(result.Details);
        Assert.Equal(before,await Snapshot(db,s));
    }
    private Task<ProfileSetup.State> Setup(ProtectionFixture key)=>ProfileSetup.Create(fixture,key);
    private async Task<AccountClosureDetails> Close(ProfileSetup.State s)
    {var r=await new AccountClosureStore(fixture).RequestAsync(new(s.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),1),default);Assert.Null(r.Error);return r.Details!;}
    private static AccountClosureCancellationRequest Request(ProfileSetup.State s)=>new(s.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),2);
    private static async Task Expire(AppDbContext db,ProfileSetup.State s)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET closure_requested_at=statement_timestamp()-interval '720 hours 1 second',closure_purge_at=statement_timestamp()-interval '1 second' WHERE id={s.InternalId}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET due_at=(SELECT closure_purge_at FROM cerberus.account WHERE id={s.InternalId}) WHERE operation_key={"account-purge/"+s.AccountId}");
    }
    private static Task<string> Snapshot(AppDbContext db,ProfileSetup.State s)=>db.Database.SqlQuery<string>($"""
        SELECT jsonb_build_object('account',(SELECT to_jsonb(a) FROM cerberus.account a WHERE id={s.InternalId}),
          'work',(SELECT to_jsonb(w) FROM cerberus.retention_work_item w WHERE operation_key={"account-purge/"+s.AccountId}),
          'records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.record r WHERE account_id={s.InternalId}),
          'profiles',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM cerberus.profile p WHERE account_id={s.InternalId}),
          'protection',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM cerberus.vault_protection p WHERE account_id={s.InternalId}),
          'challenges',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM cerberus.vault_unlock_challenge p WHERE account_id={s.InternalId}),
          'native',(SELECT jsonb_agg(to_jsonb(g) ORDER BY g.id) FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id WHERE c.account_id={s.InternalId} OR g.recipient_account_id={s.InternalId}),
          'software',(SELECT jsonb_agg(to_jsonb(g) ORDER BY id) FROM cerberus.software_grant g WHERE account_id={s.InternalId}),
          'targets',(SELECT jsonb_agg(to_jsonb(g) ORDER BY grant_id,record_id) FROM cerberus.software_grant_record g WHERE account_id={s.InternalId}),
          'terminal',(SELECT jsonb_agg(to_jsonb(t) ORDER BY resource_kind,resource_id) FROM cerberus.terminal_erasure t WHERE (resource_kind='account' AND resource_id={s.AccountId}) OR (resource_kind='record' AND resource_id IN(SELECT public_id FROM cerberus.record WHERE account_id={s.InternalId}))),
          'sessions',(SELECT jsonb_agg(to_jsonb(v) ORDER BY id) FROM cerberus.vault_access_session v WHERE account_id={s.InternalId}))::text AS "Value"
        """).SingleAsync();
}
