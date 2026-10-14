using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class NativeMutationTransactionTests(PostgresFixture fixture)
{
    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenExternalTransaction_WhenInnerMutationEnds_ThenCommitOnlyItsSavepointAndLeaveOuterOwnership(bool committed)
    {
        await using var db = fixture.CreateContext(); await using var outer = await db.Database.BeginTransactionAsync();
        var id = Guid.NewGuid();
        await using (var inner = await NativeMutationTransaction.BeginAsync(db, default))
        {
            db.Accounts.Add(new() { PublicId = id, HeimdallPublicId = Guid.NewGuid() }); await db.SaveChangesAsync();
            if (committed) await inner.CommitAsync(default);
        }
        db.ChangeTracker.Clear(); Assert.Equal(committed, await db.Accounts.AnyAsync(x => x.PublicId == id));
        await using (var outside = fixture.CreateContext()) Assert.False(await outside.Accounts.AnyAsync(x => x.PublicId == id));
        await outer.CommitAsync();
        await using var final = fixture.CreateContext(); Assert.Equal(committed, await final.Accounts.AnyAsync(x => x.PublicId == id));
    }
    [FunctionalFact]
    public async Task GivenFailedSqlWithinSavepoint_WhenDisposing_ThenRestoreUsableOuterTransactionWithoutPartialRows()
    {
        await using var db = fixture.CreateContext(); await using var outer = await db.Database.BeginTransactionAsync(); var id = Guid.NewGuid();
        await using (var inner = await NativeMutationTransaction.BeginAsync(db, default))
        {
            db.Accounts.Add(new() { PublicId = id, HeimdallPublicId = Guid.NewGuid() }); await db.SaveChangesAsync();
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("SELECT 1/0"));
        }
        db.ChangeTracker.Clear(); Assert.False(await db.Accounts.AnyAsync(x => x.PublicId == id));
        var survivor = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid() };
        db.Accounts.Add(survivor); await db.SaveChangesAsync(); await outer.CommitAsync();
        await using var final = fixture.CreateContext(); Assert.True(await final.Accounts.AnyAsync(x => x.Id == survivor.Id));
    }
    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenStandaloneMutation_WhenEnding_ThenRetainOrdinaryCommitAndRollbackSemantics(bool committed)
    {
        await using var db = fixture.CreateContext(); var id = Guid.NewGuid();
        await using (var tx = await NativeMutationTransaction.BeginAsync(db, default))
        { db.Accounts.Add(new() { PublicId = id, HeimdallPublicId = Guid.NewGuid() }); await db.SaveChangesAsync(); if (committed) await tx.CommitAsync(default); }
        await using var final = fixture.CreateContext(); Assert.Equal(committed, await final.Accounts.AnyAsync(x => x.PublicId == id));
    }
}
