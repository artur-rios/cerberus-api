using System.Data.Common;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountClosureStoreTests
{
    [FunctionalTheory]
    [InlineData("account")][InlineData("revision")][InlineData("ciphertext")][InlineData("target")][InlineData("foreignCollection")][InlineData("sibling")][InlineData("terminal")]
    public async Task GivenPostWriteStorageFault_WhenClosing_ThenRollbackBeforeCleanRetry(string fault)
    {
        using var key = new ProtectionFixture(); using var recipientKey = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, key); var recipient = await ProfileSetup.Create(fixture, recipientKey);
        var items = await AssociationSetup.Items(fixture, s);
        using var foreignKey = new ProtectionFixture(); var foreign = await ProfileSetup.Create(fixture, foreignKey);
        var foreignItems = await AssociationSetup.Items(fixture, foreign);
        await AssociationSetup.Grant(fixture, foreign, foreignKey, s, key, foreignItems.Collection);
        var sibling = await AssociationSetup.Grant(fixture, foreign, foreignKey, recipient, recipientKey, foreignItems.Collection);
        var grant = await AssociationSetup.Grant(fixture, s, key, recipient, recipientKey, items.Collection);
        await using var db = fixture.CreateContext(); var software = new SoftwareGrant { PublicId = Guid.NewGuid(), AccountId = s.InternalId,
            HeimdallSoftwarePublicId = Guid.NewGuid(), RecipientKey = [1, 2] };
        db.SoftwareGrants.Add(software); await db.SaveChangesAsync();
        db.SoftwareGrantRecords.Add(new() { AccountId = s.InternalId, GrantId = software.Id, RecordId = items.Record.Id, RecipientKeyEnvelope = [3, 4] });
        await db.SaveChangesAsync();
        var hook = new ClosureAfter(fault == "target" ? "DELETE FROM cerberus.software_grant_record" : "UPDATE cerberus.collection_grant", async c =>
        {
            await using var command = c.Connection!.CreateCommand(); command.Transaction = c.Transaction;
            command.CommandText = fault switch
            {
                "terminal" => "INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at) VALUES('record',@recordPublic,statement_timestamp())",
                "account" => "UPDATE cerberus.account SET state=0,closure_requested_at=NULL,closure_purge_at=NULL WHERE id=@owner",
                "revision" => "UPDATE cerberus.collection_grant SET revision=1 WHERE id=@grant",
                "foreignCollection" => "UPDATE cerberus.collection SET envelope='\\x01'::bytea WHERE id=@foreign",
                "sibling" => "UPDATE cerberus.collection_grant SET recipient_key_envelope='\\x01'::bytea WHERE id=@sibling",
                "ciphertext" => "UPDATE cerberus.record SET envelope='\\x01'::bytea WHERE id=@record",
                _ => "INSERT INTO cerberus.software_grant_record(account_id,grant_id,record_id,recipient_key_envelope) VALUES(@owner,@software,@record,'\\x01'::bytea)"
            };
            command.Parameters.Add(new NpgsqlParameter("owner", s.InternalId)); command.Parameters.Add(new NpgsqlParameter("grant", grant.Id));
            command.Parameters.Add(new NpgsqlParameter("record", items.Record.Id)); command.Parameters.Add(new NpgsqlParameter("software", software.Id));
            command.Parameters.Add(new NpgsqlParameter("recordPublic", items.Record.PublicId));
            command.Parameters.Add(new NpgsqlParameter("foreign", foreignItems.Collection.Id)); command.Parameters.Add(new NpgsqlParameter("sibling", sibling.Id));
            await command.ExecuteNonQueryAsync();
        }, fault == "revision" ? grant.PublicId : null);
        var result = await new AccountClosureStore(new ProfileSetup.Factory(fixture, hook)).RequestAsync(Request(s), default);
        Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Details);
        var saved = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == s.InternalId);
        Assert.Equal(AccountState.Active, saved.State); Assert.Equal(1, saved.Revision); Assert.Equal(1, saved.RevocationGeneration); Assert.Null(saved.ClosureRequestedAt);
        var original = await db.CollectionGrants.AsNoTracking().SingleAsync(x => x.Id == grant.Id);
        Assert.Equal(CollectionGrantState.Active, original.State); Assert.Equal(1, original.Revision); Assert.NotEmpty(original.RecipientKeyEnvelope);
        Assert.Equal(items.Record.Envelope, await db.Records.Where(x => x.Id == items.Record.Id).Select(x => x.Envelope).SingleAsync());
        Assert.True(await db.SoftwareGrantRecords.AnyAsync(x => x.GrantId == software.Id));
        Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="record"&&x.ResourceId==items.Record.PublicId));
        Assert.Equal(foreignItems.Collection.Envelope, await db.Collections.Where(x => x.Id == foreignItems.Collection.Id).Select(x => x.Envelope).SingleAsync());
        Assert.Equal(sibling.RecipientKeyEnvelope, await db.CollectionGrants.Where(x => x.Id == sibling.Id).Select(x => x.RecipientKeyEnvelope).SingleAsync());
        Assert.False(await db.RetentionWorkItems.AnyAsync(x => x.OperationKey == "account-purge/" + s.AccountId));
        Assert.Null((await new AccountClosureStore(fixture).RequestAsync(Request(s), default)).Error);
    }

    [FunctionalFact]
    public async Task GivenCorruptedWorkAfterSaving_WhenClosing_ThenRollbackAndPermitRetry()
    {
        using var key = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture,key);
        var hook = new WorkFault(s.AccountId);
        var result = await new AccountClosureStore(new ProfileSetup.Factory(fixture,hook)).RequestAsync(Request(s),default);
        Assert.Equal("persistence_unavailable",result.Error);
        await using var db = fixture.CreateContext(); Assert.Equal(AccountState.Active,await db.Accounts.Where(x=>x.Id==s.InternalId).Select(x=>x.State).SingleAsync());
        Assert.False(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="account-purge/"+s.AccountId));
        Assert.Null((await new AccountClosureStore(fixture).RequestAsync(Request(s),default)).Error);
    }
    private sealed class WorkFault(Guid id) : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data,int result,CancellationToken ct=default)
        {
            await data.Context!.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET attempts={int.MaxValue} WHERE operation_key={"account-purge/"+id}",ct);
            return result;
        }
    }
    private sealed class ClosureAfter(string match, Func<DbCommand, Task> action, Guid? target = null) : DbCommandInterceptor
    {
        private bool used;
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int result, CancellationToken ct = default)
        { if (!used && c.CommandText.Contains(match, StringComparison.Ordinal)
                && (target is null || c.Parameters.Cast<DbParameter>().Any(x => Equals(x.Value,target.Value)))) { used = true; await action(c); } return result; }
    }
}
