using System.Data.Common;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
namespace ArturRios.Cerberus.Data.Tests;

public partial class ResourceErasureTests
{
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenClaimedOriginalTrashJob_WhenReplaying_ThenKeepItsFenceUntilWorkerCompletes(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);await using var db=fixture.CreateContext();
        var job=await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="trash/"+root.OperationId);job.DueAt=DateTimeOffset.UtcNow.AddMinutes(-1);await db.SaveChangesAsync();
        var jobs=new RetentionWorkStore(fixture);var claim=await jobs.TryClaimAsync(job.PublicId,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(5),default);Assert.NotNull(claim);
        await new TerminalErasureStore(fixture).ReapplyAsync(new(root.ResourceId,kind,Deleted),default);Assert.False(await Exists(kind,root.ResourceId));Assert.False(await db.TrashOperations.AnyAsync(x=>x.PublicId==root.OperationId));
        var retained=await db.RetentionWorkItems.AsNoTracking().SingleOrDefaultAsync(x=>x.PublicId==job.PublicId);Assert.NotNull(retained);Assert.Equal(claim!.Token,retained.ClaimToken);Assert.Null(retained.CompletedAt);
        Assert.True(await jobs.TryCompleteAsync(claim,DateTimeOffset.UtcNow,default));
    }
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenDamagedOpaqueOwnedCipher_WhenReplaying_ThenPhysicalPrivacyDeletionNeedsNoDecryption(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);await using var db=fixture.CreateContext();
        if(kind=="record")await db.Records.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()));
        if(kind=="folder")await db.Folders.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()));
        if(kind=="collection")await db.Collections.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()));
        if(kind=="profile")await db.Profiles.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()).SetProperty(r=>r.KeyWrappers,"bad"u8.ToArray()));
        await new TerminalErasureStore(fixture).ReapplyAsync(new(root.ResourceId,kind,Deleted),default);Assert.False(await Exists(kind,root.ResourceId));
    }
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenDatabaseTriggerPreventsPhysicalDelete_WhenReplaying_ThenNeverAcknowledgeCipherErasure(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);var before=await TrashRestoreSetup.Snapshot(fixture,s);await using var db=fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION cerberus.fixture_skip_erasure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF OLD.public_id::text=current_setting('cerberus.fixture_skip_erasure',true) THEN RETURN NULL; END IF; RETURN OLD; END $$
            """);
        await db.Database.ExecuteSqlRawAsync(Trigger(kind,false));
        try {
            var hook=new SkipDelete(kind,root.ResourceId);var store=new TerminalErasureStore(new ProfileSetup.Factory(fixture,hook));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>store.ReapplyAsync(new(root.ResourceId,kind,Deleted),default));Assert.True(hook.Reached);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind==kind&&x.ResourceId==root.ResourceId));
        }
        finally{await db.Database.ExecuteSqlRawAsync(Trigger(kind,true));await db.Database.ExecuteSqlRawAsync("DROP FUNCTION cerberus.fixture_skip_erasure()");}
        await new TerminalErasureStore(fixture).ReapplyAsync(new(root.ResourceId,kind,Deleted),default);Assert.False(await Exists(kind,root.ResourceId));
    }
    [FunctionalFact]
    public async Task GivenFolderRootErasedBeforeRetainedRecord_WhenReplayingLastMember_ThenRetireTheEmptyCascadeOperation()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var a=await AssociationSetup.Items(fixture,s);
        await using(var db=fixture.CreateContext()){await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));}
        var root=await TrashRestoreSetup.TrashAsync(fixture,s,"folder",a.Folder.PublicId,1,a.Folder.Id,a.Folder.EditedAt);var store=new TerminalErasureStore(fixture);
        await store.ReapplyAsync(new(a.Folder.PublicId,"folder",Deleted),default);await using(var db=fixture.CreateContext()){Assert.True(await db.TrashOperations.AnyAsync(x=>x.PublicId==root.OperationId));Assert.True(await db.Records.AnyAsync(x=>x.PublicId==a.Record.PublicId));}
        await store.ReapplyAsync(new(a.Record.PublicId,"record",Deleted),default);Assert.False(await Exists("record",a.Record.PublicId));await TrashRestoreSetup.NoOperation(fixture,root.OperationId);
    }
    private static string Trigger(string kind,bool drop)=>(kind,drop) switch{
        ("record",false)=>"CREATE TRIGGER fixture_skip_erasure BEFORE DELETE ON cerberus.record FOR EACH ROW EXECUTE FUNCTION cerberus.fixture_skip_erasure()",
        ("folder",false)=>"CREATE TRIGGER fixture_skip_erasure BEFORE DELETE ON cerberus.folder FOR EACH ROW EXECUTE FUNCTION cerberus.fixture_skip_erasure()",
        ("collection",false)=>"CREATE TRIGGER fixture_skip_erasure BEFORE DELETE ON cerberus.collection FOR EACH ROW EXECUTE FUNCTION cerberus.fixture_skip_erasure()",
        ("profile",false)=>"CREATE TRIGGER fixture_skip_erasure BEFORE DELETE ON cerberus.profile FOR EACH ROW EXECUTE FUNCTION cerberus.fixture_skip_erasure()",
        ("record",true)=>"DROP TRIGGER fixture_skip_erasure ON cerberus.record",("folder",true)=>"DROP TRIGGER fixture_skip_erasure ON cerberus.folder",
        ("collection",true)=>"DROP TRIGGER fixture_skip_erasure ON cerberus.collection",_=>"DROP TRIGGER fixture_skip_erasure ON cerberus.profile"};
    private sealed class SkipDelete(string kind,Guid id):DbCommandInterceptor
    {
        internal bool Reached{get;private set;}
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default)
        {
            if(!Reached&&c.CommandText.StartsWith("DELETE FROM cerberus."+kind+" ",StringComparison.Ordinal)){
                Reached=true;await using var command=c.Connection!.CreateCommand();command.Transaction=c.Transaction;command.CommandText="SELECT set_config('cerberus.fixture_skip_erasure',@id,true)";command.Parameters.Add(new NpgsqlParameter("id",id.ToString("D")));await command.ExecuteNonQueryAsync(ct);}
            return r;
        }
    }
}
