from pathlib import Path
p=Path('tests/Infrastructure/ArturRios.Cerberus.Data.Tests/RecordPurgeHandlerTests.cs');s=p.read_text();a=s.index('    private async Task<(RetentionWorkItem, RetentionClaim)> Pending')
s=s[:a]+'''    [FunctionalTheory]
    [InlineData("record")][InlineData("trash_entry")][InlineData("retention_work_item")][InlineData("trash_operation")]
    public async Task GivenClaimExpiresAfterCheckBeforePhysicalStatement_WhenPurging_ThenThatStatementCannotRemoveRows(string table)
    {
        using var w=await PermanentDeleteSetup.Create(fixture,"wide",true);
        var (work,claim)=await Pending(w,"record");
        var deadline=DateTimeOffset.UtcNow.AddSeconds(2);
        await using var db=fixture.CreateContext();
        await db.RetentionWorkItems.Where(x=>x.Id==work.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ClaimExpiresAt,deadline));
        var before=await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId);
        var hook=new ExpireBeforeMutation(table,deadline);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new RecordPurgeHandler(new ProfileSetup.Factory(fixture,hook),Ledger).ExecuteAsync(claim,default));
        Assert.True(hook.Reached);
        Assert.Equal(0,hook.Affected);
        Assert.Equal(before,await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId));
        Assert.Single(Directory.EnumerateFiles(directory,"*.json"));
    }
    private sealed class ExpireBeforeMutation(string table,DateTimeOffset deadline):DbCommandInterceptor
    {
        internal bool Reached{get;private set;}
        internal int Affected{get;private set;}=-1;
        private bool Match(DbCommand command)=>command.CommandText.Contains("DELETE FROM cerberus."+table+" ",StringComparison.Ordinal)
            || command.CommandText.Contains("DELETE FROM cerberus."+table+"\\n",StringComparison.Ordinal)
            || command.CommandText.Contains("DELETE FROM cerberus."+table+" AS",StringComparison.Ordinal);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {
            if(!Reached && Match(command)){Reached=true;var left=deadline-DateTimeOffset.UtcNow;if(left>TimeSpan.Zero)await Task.Delay(left+TimeSpan.FromMilliseconds(50),ct);}
            return result;
        }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,CommandExecutedEventData data,int result,CancellationToken ct=default)
        {if(Reached && Match(command))Affected=result;return ValueTask.FromResult(result);}
    }

''' +s[a:];p.write_text(s)
