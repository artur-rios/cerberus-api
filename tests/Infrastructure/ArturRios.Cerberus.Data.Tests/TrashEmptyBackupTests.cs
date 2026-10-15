using System.Text.Json;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class TrashEmptyBackupTests
{
    [FunctionalFact]
    public async Task GivenCompletedMixedTrashPurgeAndOlderRealBackup_WhenRestoring_ThenReplayAllCipherErasuresBeforeTraffic()
    {
        var isolated=new PostgresFixture();var directory=Directory.CreateTempSubdirectory("cerberus-uc52-backup-ledger-").FullName;
        try {
            await isolated.InitializeAsync();using var keys=new ProtectionFixture();using var remote=new ProtectionFixture();var w=await TrashEmptyNativeSetup.Create(isolated,keys,remote);var before=ExpectedReplaySurvivors(await TrashEmptyNativeSetup.Survivors(isolated,w),w.Owned.Collection.Id);var backup=await isolated.BackupAsync();
            await TrashEmptyNativeSetup.Trash(isolated,w);var ledger=new FileErasureLedger(directory);
            var result=await new TrashEmptyStore(isolated,ledger,new CerberusOptions{RetentionInterval="00:05:00"},TimeProvider.System).EmptyAsync(new(w.Owner.Actor,w.Owner.Verifier,1),default);Assert.Null(result.Error);Assert.Equal(7,result.Data!.TotalPurged);Assert.Equal(7,Directory.GetFiles(directory,"*.json").Length);
            await isolated.RestoreAsync(backup);await using(var old=isolated.CreateContext()){Assert.True(await old.Records.AnyAsync(x=>x.Id==w.Owned.Record.Id));Assert.True(await old.Folders.AnyAsync(x=>x.Id==w.Owned.Folder.Id));Assert.True(await old.Collections.AnyAsync(x=>x.Id==w.Owned.Collection.Id));Assert.True(await old.Profiles.AnyAsync(x=>x.Id==w.Profile));Assert.False((await old.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==w.SelectedVerifier)).Revoked);}
            var gate=new RestoreTrafficGate();var checkedBeforeTraffic=false;var verifier=new Verify(async()=>{Assert.False(gate.IsReady);await TrashEmptyNativeSetup.Gone(isolated,w);checkedBeforeTraffic=true;return true;});
            Assert.True(await new RestoreReconciler(ledger,new TerminalErasureStore(isolated),verifier,gate).ReconcileAsync(default));Assert.True(checkedBeforeTraffic);Assert.True(gate.IsReady);Assert.True(JsonNode.DeepEquals(before,ReplaySurvivors(await TrashEmptyNativeSetup.Survivors(isolated,w))),"Restored survivor values differ from the backup after the allowed erased-collection link removal.");await TrashEmptyNativeSetup.Gone(isolated,w);
            await using(var check=isolated.CreateContext()){var selected=await check.VaultAccessSessions.AsNoTracking().SingleAsync(x=>x.HandleVerifier==w.SelectedVerifier);Assert.Equal(w.Profile,selected.ProfileId);Assert.True(selected.Revoked);}
            var recreated=await new RecordCreateStore(isolated).CreateAsync(new(w.Owner.Actor,w.Owner.Verifier,new(w.Owned.Record.PublicId,ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch,[],null)),default);Assert.Equal("revision_conflict",recreated.Error);
        }
        finally{await isolated.DisposeAsync();Directory.Delete(directory,true);}
    }
    private static JsonNode ExpectedReplaySurvivors(string snapshot,long erasedCollection)
    {
        // Replay preserves the restored backup's native revisions; the only
        // foreign-row reduction is the link to the permanently erased collection.
        var root=JsonNode.Parse(snapshot)!.AsObject();var foreign=JsonNode.Parse(root["Foreign"]!.GetValue<string>())!.AsObject();
        var links=foreign["PC"]!.AsArray();Assert.Single(links,x=>x!["CollectionId"]!.GetValue<long>()==erasedCollection);
        foreach(var link in links.Where(x=>x!["CollectionId"]!.GetValue<long>()==erasedCollection).ToArray())links.Remove(link);
        root["Foreign"]=foreign;return root;
    }
    private static JsonNode ReplaySurvivors(string snapshot)
    {
        var root=JsonNode.Parse(snapshot)!.AsObject();root["Foreign"]=JsonNode.Parse(root["Foreign"]!.GetValue<string>());return root;
    }
    private sealed class Verify(Func<Task<bool>> callback):IRestoreAuthorizationVerifier
    {public Task<bool> VerifyAsync(CancellationToken cancellationToken)=>callback();}
}
