using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("Retention maintenance PostgreSQL")]
public partial class RetentionMaintenanceTests(PostgresFixture fixture):IAsyncLifetime
{
    private readonly List<long> owners=[];
    private async Task<ProfileSetup.State> Owner(ProtectionFixture keys){var s=await ProfileSetup.Create(fixture,keys);owners.Add(s.InternalId);return s;}
    private Task<RetentionRunResult> Run(IDbContextFactory<AppDbContext>? target=null,int pageSize=100)
    {
        target??=fixture;var maintenance=new RetentionMaintenance(target);
        return new RetentionRunner(new RetentionWorkStore(target),new RetentionExecutor([]),TimeProvider.System,maintenance).RunOnceAsync(TimeSpan.FromMinutes(5),pageSize,default);
    }
    [FunctionalFact]
    public async Task GivenMissingExpiredTrashJob_WhenRunningMaintenance_ThenRepairOriginalDeadlineWithoutNativeIntent()
    {
        using var keys=new ProtectionFixture();var s=await Owner(keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));await using var db=fixture.CreateContext();var op=await db.TrashOperations.AsNoTracking().SingleAsync(x=>x.PublicId==root.OperationId);await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+root.OperationId).ExecuteDeleteAsync();
        var before=await TrashRestoreSetup.Resource(fixture,root);var result=await Run();Assert.Equal(1,result.ScheduledJobs);Assert.Equal(0,result.ExpiredStreams);
        var job=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="trash/"+root.OperationId);Assert.Equal(op.PurgeAt,job.DueAt);Assert.Null(job.CompletedAt);Assert.Equal(before.GetRawText(),(await TrashRestoreSetup.Resource(fixture,root)).GetRawText());Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="record"&&x.ResourceId==root.ResourceId));
        var snapshot=System.Text.Json.JsonSerializer.Serialize(job);Assert.Equal(0,(await Run()).ScheduledJobs);Assert.Equal(snapshot,System.Text.Json.JsonSerializer.Serialize(await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.PublicId==job.PublicId)));
    }
    [FunctionalFact]
    public async Task GivenMissingDueClosureJob_WhenRunningMaintenance_ThenRebuildOnlyOriginalAccountWork()
    {
        using var keys=new ProtectionFixture();var s=await Owner(keys);Assert.Null((await new AccountClosureStore(fixture).RequestAsync(new(s.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),1),default)).Error);
        await using var db=fixture.CreateContext();var due=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp()-interval '1 second' AS \"Value\"").SingleAsync();await db.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ClosureRequestedAt,due.AddDays(-30)).SetProperty(a=>a.ClosurePurgeAt,due));await db.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+s.AccountId).ExecuteDeleteAsync();
        var result=await Run();Assert.Equal(1,result.ScheduledJobs);var job=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="account-purge/"+s.AccountId);Assert.Equal(due,job.DueAt);Assert.Null(job.CompletedAt);Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==s.AccountId));Assert.True(await db.Accounts.AnyAsync(x=>x.Id==s.InternalId));
    }
    [FunctionalTheory][InlineData("future")][InlineData("malformed")][InlineData("completed")]
    public async Task GivenFutureMalformedOrCompletedOriginalWork_WhenMaintaining_ThenNeverInventOrReopenExpiry(string state)
    {
        using var keys=new ProtectionFixture();var s=await Owner(keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await using var db=fixture.CreateContext();
        if(state!="future")await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));
        if(state=="malformed")await db.TrashOperations.Where(x=>x.PublicId==root.OperationId).ExecuteUpdateAsync(x=>x.SetProperty(o=>o.DeletedAt,DateTimeOffset.UtcNow.AddDays(-31)));
        if(state=="completed")await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+root.OperationId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.CompletedAt,DateTimeOffset.UtcNow));
        else await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+root.OperationId).ExecuteDeleteAsync();
        var before=await TrashRestoreSetup.Snapshot(fixture,s);var result=await Run();Assert.Equal(0,result.ScheduledJobs);Assert.Equal(0,result.ExpiredStreams);Assert.Equal(0,result.Completed);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));
    }
    [FunctionalFact]
    public async Task GivenExpiredAndCurrentStreams_WhenMaintaining_ThenCascadeOnlyExpiredStreamMetadataAndKeepUploadIdempotency()
    {
        using var keys=new ProtectionFixture();var s=await Owner(keys);var profile=await AssociationSetup.Profile(fixture,s,keys);await using var db=fixture.CreateContext();var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync();
        var expired=new SyncStream{PublicId=Guid.NewGuid(),AccountId=s.InternalId,ProfileId=profile,CreatedAt=now.AddDays(-2),ExpiresAt=now.AddSeconds(-1),PageSize=10,VisibilityStamp=new byte[32]};var current=new SyncStream{PublicId=Guid.NewGuid(),AccountId=s.InternalId,ProfileId=profile,CreatedAt=now,ExpiresAt=now.AddDays(1),PageSize=10,VisibilityStamp=new byte[32]};db.AddRange(expired,current);await db.SaveChangesAsync();
        var resource=Guid.NewGuid();db.SyncInventories.Add(new(){StreamId=expired.Id,ResourceKind="record",ResourceId=resource,Fingerprint=new byte[32]});db.SyncChanges.Add(new(){StreamId=expired.Id,ResourceKind="record",ResourceId=resource,WindowVersion=1,EventType="upsert"});db.SyncOperations.Add(new(){AccountId=s.InternalId,OperationId=Guid.NewGuid(),ResourceKind="record",ResourceId=resource,Action="create",Status="denied",RequestFingerprint=new byte[32],CreatedAt=now.AddYears(-1)});await db.SaveChangesAsync();
        var result=await Run();Assert.Equal(1,result.ExpiredStreams);Assert.Equal(0,result.ScheduledJobs);Assert.False(await db.SyncStreams.AnyAsync(x=>x.Id==expired.Id));Assert.True(await db.SyncStreams.AnyAsync(x=>x.Id==current.Id));Assert.False(await db.SyncInventories.AnyAsync(x=>x.StreamId==expired.Id));Assert.False(await db.SyncChanges.AnyAsync(x=>x.StreamId==expired.Id));Assert.True(await db.SyncOperations.AnyAsync(x=>x.AccountId==s.InternalId&&x.ResourceId==resource));Assert.Equal(0,(await Run()).ExpiredStreams);
    }
    [FunctionalFact]
    public async Task GivenBusyExpiredStream_WhenMaintaining_ThenSkipWithoutBlockingAndRetryAfterDownloadReleases()
    {
        using var keys=new ProtectionFixture();var s=await Owner(keys);var profile=await AssociationSetup.Profile(fixture,s,keys);await using var held=fixture.CreateContext();var stream=new SyncStream{PublicId=Guid.NewGuid(),AccountId=s.InternalId,ProfileId=profile,CreatedAt=DateTimeOffset.UtcNow.AddDays(-2),ExpiresAt=DateTimeOffset.UtcNow.AddDays(-1),PageSize=10,VisibilityStamp=new byte[32]};held.Add(stream);await held.SaveChangesAsync();await using var tx=await held.Database.BeginTransactionAsync();await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.sync_stream WHERE id={stream.Id} FOR UPDATE");
        try{var result=await Run().WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,result.ExpiredStreams);}finally{await tx.CommitAsync();}
        Assert.Equal(1,(await Run()).ExpiredStreams);
    }
    [FunctionalFact]
    public async Task GivenSeveralMissingDueJobs_WhenMaintainingBoundedPage_ThenProgressWithoutExceedingPageSize()
    {
        using var keys=new ProtectionFixture();var s=await Owner(keys);var roots=new List<TrashRestoreSetup.Root>();
        for(var i=0;i<3;i++){var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));roots.Add(root);}
        await using var db=fixture.CreateContext();var original=roots.Select(x=>"trash/"+x.OperationId).ToArray();await db.RetentionWorkItems.Where(x=>original.Contains(x.OperationKey)).ExecuteDeleteAsync();
        Assert.Equal(1,(await Run(pageSize:1)).ScheduledJobs);Assert.Equal(1,await db.RetentionWorkItems.CountAsync(x=>original.Contains(x.OperationKey)));Assert.Equal(1,(await Run(pageSize:1)).ScheduledJobs);Assert.Equal(1,(await Run(pageSize:1)).ScheduledJobs);Assert.Equal(3,await db.RetentionWorkItems.CountAsync(x=>original.Contains(x.OperationKey)));Assert.Equal(0,(await Run(pageSize:1)).ScheduledJobs);
    }
    public Task InitializeAsync()=>Task.CompletedTask;
    public async Task DisposeAsync()
    {
        if(owners.Count==0)return;await using var db=fixture.CreateContext();var ids=await db.Accounts.Where(x=>owners.Contains(x.Id)).Select(x=>x.PublicId).ToListAsync();var operations=await db.TrashOperations.Where(x=>owners.Contains(x.AccountId)).Select(x=>x.PublicId).ToListAsync();var keys=operations.Select(x=>"trash/"+x).Concat(ids.SelectMany(x=>new[]{"account-purge/"+x,"account-erase/"+x,"trash-erase/"+x})).ToArray();await db.RetentionWorkItems.Where(x=>keys.Contains(x.OperationKey)).ExecuteDeleteAsync();await db.Accounts.Where(x=>owners.Contains(x.Id)).ExecuteDeleteAsync();
    }
}

[CollectionDefinition("Retention maintenance PostgreSQL",DisableParallelization=true)]
public sealed class RetentionMaintenancePostgresCollection:ICollectionFixture<PostgresFixture>;
