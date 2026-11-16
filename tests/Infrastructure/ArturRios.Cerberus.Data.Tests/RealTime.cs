using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

// Bounds (session expiries, claim leases, deadlines, freshness) that must pass while a store is in the middle of an
// operation. The stores compare every bound with the database's clock (statement_timestamp() after their lock waits,
// by design: one clock for every API instance), so a test cannot fake time. It moves the bound instead, as late as it
// can, so that it waits as little real time as possible:
//
// - Right before (or after) one of the store's statements, a hook sets the bound to clock_timestamp() in the store's
//   own transaction (ExpireNow). The store's next statement starts later on the same clock: no lead, no wait.
// - Where the store waits on a lock the test holds, the bound is armed one short Lead ahead at that very statement
//   (ArmAtWait), and the test lets the store go once the database clock has passed it (Past).
// - Where the bound must be in place before the store starts (a request value, a deadline another operation is
//   admitted against), it is set SetupLead ahead: enough for the store's whole path to that point on a busy CI agent.
//   If that ever ran out first, the store would refuse early and the test fail visibly (its signal or hook never
//   reached, or the expected success missing), never pass by accident.
internal static class RealTime
{
    // Time between arming a bound and the store's statement that must still see it current: one round trip.
    internal static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(300);

    // Time a bound set before the store starts must last: the store's path up to the statement under test. The
    // shortest lead that already ran stably in CI for a whole store path (the whole-second bounds, set to the next
    // second but one, were at least one second ahead).
    internal static readonly TimeSpan SetupLead = TimeSpan.FromSeconds(1);

    // How far past the bound a test waits before it lets the store go on, so the database clock is surely past it.
    internal static readonly TimeSpan Margin = TimeSpan.FromMilliseconds(100);

    // The database clock minus this machine's, measured once when the server starts. In CI they are the same clock;
    // a Docker VM's clock can drift from its host's, and the short lead would not absorb that.
    private static long _offsetTicks;

    internal static void Calibrate(DateTimeOffset databaseNow, DateTimeOffset before, DateTimeOffset after) =>
        Interlocked.Exchange(ref _offsetTicks, (databaseNow - (before + (after - before) / 2)).Ticks);

    // This machine's estimate of the database clock.
    internal static DateTimeOffset Now => DateTimeOffset.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    // A bound one lead from now (Lead by default).
    internal static DateTimeOffset Soon(TimeSpan? lead = null) => Now + (lead ?? Lead);

    // The first whole second (Unix time) at least one lead from now, for bounds the protocol keeps in whole seconds.
    internal static long SoonSeconds(TimeSpan? lead = null) => (Soon(lead).ToUnixTimeMilliseconds() + 999) / 1000;

    // Waits until the database clock is past the bound.
    internal static async Task Past(DateTimeOffset bound, CancellationToken ct = default)
    {
        var wait = bound + Margin - Now;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }

    internal static Task Past(long unixSeconds, CancellationToken ct = default) =>
        Past(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), ct);

    // An authentication time (Unix seconds) that is fresh now and goes stale at the returned instant, at least
    // SetupLead from now: fresh means fewer than windowSeconds whole seconds old on the database clock.
    internal static (long IssuedAt, DateTimeOffset StaleAt) StaleSoon(int windowSeconds)
    {
        var stale = SoonSeconds(SetupLead);
        return (stale - windowSeconds, DateTimeOffset.FromUnixTimeSeconds(stale));
    }

    // Sets a bound to clock_timestamp() on the store's own connection and transaction, right where a hook stopped it.
    // The transaction already holds every lock the store took, and a refusing store rolls the change back.
    internal static Task ExpireNow(DbCommand at, string sql, params (string Name, object Value)[] parameters) =>
        Execute(at, sql, parameters);

    // Runs a statement on the store's own connection and transaction.
    internal static async Task Execute(DbCommand at, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = at.Connection!.CreateCommand();
        command.Transaction = at.Transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        Assert.True(await command.ExecuteNonQueryAsync() > 0, "The bound to move was not found.");
    }

    // The common bound: the presented session's expiry.
    internal static Task ExpireSession(DbCommand at, string verifier) =>
        ExpireNow(at, "UPDATE cerberus.vault_access_session SET expires_at=clock_timestamp() WHERE handle_verifier=@verifier", ("verifier", verifier));

    // Arms the presented session's expiry at the store's lock wait: in the store's transaction, or in the holder's when
    // the holder holds the session row (the store's write would wait on it; the holder's commits with it).
    internal static ArmAtWait ArmSession(Task reached, string verifier, DbContext? holder = null) =>
        Arm(reached, holder, "UPDATE cerberus.vault_access_session SET expires_at=@deadline WHERE handle_verifier=@verifier", ("verifier", verifier));

    // The presented session expiring while the store waits on a lock the test holds. It is armed at the wait in the
    // store's transaction, which rolls it back when the store refuses. When the holder holds the session row itself,
    // the store could not write it there, and a write in the holder's transaction would commit with it and show in
    // the tests' before/after snapshots; so the expiry is committed SetupLead ahead before the snapshot is taken.
    internal static async Task<WaitExpiry> SessionExpiresDuringWait(PostgresFixture fixture, string verifier, bool holderHoldsSession)
    {
        if (!holderHoldsSession) return new(verifier, null);
        var deadline = Soon(SetupLead);
        await using var db = fixture.CreateContext();
        Assert.True(await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cerberus.vault_access_session SET expires_at={deadline} WHERE handle_verifier={verifier}") > 0, "The session was not found.");
        return new(verifier, deadline);
    }

    internal sealed class WaitExpiry(string verifier, DateTimeOffset? committed)
    {
        private ArmAtWait? _arm;

        // The interceptor to register after the one that completes `reached` (null when the expiry is committed).
        internal IInterceptor? At(Task reached) => committed is null ? _arm = ArmSession(reached, verifier) : null;

        // Waits until the session has expired on the database clock.
        internal async Task Past(CancellationToken ct = default) => await RealTime.Past(committed ?? await _arm!.Deadline, ct);
    }

    // Arms any bound: sql sets it to @deadline.
    internal static ArmAtWait Arm(Task reached, DbContext? holder, string sql, params (string Name, object Value)[] parameters) =>
        new(reached, async (command, deadline) =>
        {
            (string, object)[] all = [.. parameters, ("deadline", deadline)];
            if (holder is null) { await Execute(command, sql, all); return; }
            var values = all.Select(x => (object)new NpgsqlParameter(x.Item1, x.Item2)).ToArray();
            Assert.True(await holder.Database.ExecuteSqlRawAsync(sql, values) > 0, "The bound to move was not found.");
        });

    // Arms a bound one Lead ahead at the statement a store is about to wait on: the first command it runs once
    // `reached` has completed. Register it after the interceptor that completes `reached` for that statement (EF calls
    // interceptors in order), so both see the same command. The arm runs in the store's transaction unless it is given
    // another way to write (the holder's transaction, when the holder holds the bound's row).
    internal sealed class ArmAtWait(Task reached, Func<DbCommand, DateTimeOffset, Task> arm) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<DateTimeOffset> _deadline = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        // The armed bound, once the store has reached its wait.
        internal Task<DateTimeOffset> Deadline => _deadline.Task;

        private async Task Check(DbCommand command)
        {
            if (!reached.IsCompleted || Interlocked.Exchange(ref _armed, 1) == 1) return;
            try
            {
                var deadline = Soon();
                await arm(command, deadline);
                _deadline.TrySetResult(deadline);
            }
            catch (Exception e) { _deadline.TrySetException(e); throw; }
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { await Check(command); return result; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { await Check(command); return result; }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { await Check(command); return result; }
    }
}
