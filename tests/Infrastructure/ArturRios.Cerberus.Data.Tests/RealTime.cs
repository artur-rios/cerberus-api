using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

// Bounds (session expiries, claim leases, deadlines, freshness) that must pass while a store is in the middle of an
// operation. The stores compare every bound with the database's clock (statement_timestamp() after their lock waits,
// by design: one clock for every API instance), so a test cannot fake time. It waits as little as it can instead:
//
// - Where a hook stops the store right before a statement whose own SQL guard is under test, the hook sets the bound
//   to clock_timestamp() in the store's transaction (ExpireNow): the statement starts later on the same clock, so no
//   wait. Only where the store does not compare the bound with a value it read before the hook (then the value would
//   be stale); a refusing store rolls the write back.
// - Otherwise the bound is committed SetupLead ahead before the store starts, as the store would find it, and the test
//   lets the store go once the database clock has passed it (Past). If the lead ever ran out early the store would
//   refuse before the statement under test, and the test fail visibly (its signal or hook never reached, or the
//   expected success missing).
internal static class RealTime
{
    // A bound that only has to outlast one or two statements (a read store's single query, an armed row wait).
    internal static readonly TimeSpan s_lead = TimeSpan.FromMilliseconds(300);

    // Time a bound set before the store starts must last: the test's last setup statements and the store's path up to
    // the statement under test. The shortest lead that already ran stably in CI for such a path (the whole-second
    // bounds, set to the next second but one, were at least one second ahead).
    internal static readonly TimeSpan s_setupLead = TimeSpan.FromSeconds(1);

    // How far past the bound a test waits before it lets the store go on, so the database clock is surely past it.
    internal static readonly TimeSpan s_margin = TimeSpan.FromMilliseconds(100);

    // The database clock minus this machine's, measured once when the server starts. In CI they are the same clock;
    // a Docker VM's clock can drift from its host's, and the short leads would not absorb that.
    private static long s_offsetTicks;

    internal static void Calibrate(DateTimeOffset databaseNow, DateTimeOffset before, DateTimeOffset after) =>
        Interlocked.Exchange(ref s_offsetTicks, (databaseNow - (before + (after - before) / 2)).Ticks);

    // This machine's estimate of the database clock.
    internal static DateTimeOffset Now => DateTimeOffset.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref s_offsetTicks));

    // A bound one lead from now (Lead by default).
    internal static DateTimeOffset Soon(TimeSpan? lead = null) => Now + (lead ?? s_lead);

    // The first whole second (Unix time) at least one lead from now, for bounds the protocol keeps in whole seconds.
    internal static long SoonSeconds(TimeSpan? lead = null) => (Soon(lead).ToUnixTimeMilliseconds() + 999) / 1000;

    // Waits until the database clock is past the bound.
    internal static async Task Past(DateTimeOffset bound, CancellationToken ct = default)
    {
        var wait = bound + s_margin - Now;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);
    }

    internal static Task Past(long unixSeconds, CancellationToken ct = default) =>
        Past(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), ct);

    // An authentication time (Unix seconds) that is fresh now and goes stale at the returned instant, at least
    // SetupLead from now: fresh means fewer than windowSeconds whole seconds old on the database clock.
    internal static (long IssuedAt, DateTimeOffset StaleAt) StaleSoon(int windowSeconds)
    {
        var stale = SoonSeconds(s_setupLead);
        return (stale - windowSeconds, DateTimeOffset.FromUnixTimeSeconds(stale));
    }

    // Commits the presented session's expiry SetupLead ahead, before the store starts.
    internal static async Task<DateTimeOffset> SessionExpiresSoon(PostgresFixture fixture, string verifier)
    {
        var deadline = Soon(s_setupLead);
        await using var db = fixture.CreateContext();
        Assert.True(await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cerberus.vault_access_session SET expires_at={deadline} WHERE handle_verifier={verifier}") > 0, "The session was not found.");
        return deadline;
    }

    // Sets a bound to clock_timestamp() on the store's own connection and transaction, right where a hook stopped it.
    internal static Task ExpireNow(DbCommand at, string sql, params (string Name, object Value)[] parameters) =>
        Execute(at, sql, parameters);

    // The presented session's expiry, set as ExpireNow does.
    internal static Task ExpireSession(DbCommand at, string verifier) =>
        ExpireNow(at, "UPDATE cerberus.vault_access_session SET expires_at=clock_timestamp() WHERE handle_verifier=@verifier", ("verifier", verifier));

    private static async Task Execute(DbCommand at, string sql, (string Name, object Value)[] parameters)
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

    // Arms a bound one Lead ahead in the holder's transaction (the holder holds the bound's row) when the store reaches
    // the statement that waits on it: the first command the store runs once `reached` has completed. Only for a store
    // that first reads the bound with that statement, after the holder commits. Register it after the interceptor that
    // completes `reached` (EF calls interceptors in order), so both see the same command. sql sets the bound to
    // @deadline.
    internal static ArmAtWait ArmInHolder(Task reached, DbContext holder, string sql, params (string Name, object Value)[] parameters) =>
        new(reached, async deadline =>
        {
            object[] values = [.. parameters.Select(x => new NpgsqlParameter(x.Name, x.Value)), new NpgsqlParameter("deadline", deadline)];
            Assert.True(await holder.Database.ExecuteSqlRawAsync(sql, values) > 0, "The bound to move was not found.");
        });

    internal sealed class ArmAtWait(Task reached, Func<DateTimeOffset, Task> arm) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<DateTimeOffset> _deadline = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        // The armed bound, once the store has reached its wait.
        internal Task<DateTimeOffset> Deadline => _deadline.Task;

        private async Task Check()
        {
            if (!reached.IsCompleted || Interlocked.Exchange(ref _armed, 1) == 1)
                return;
            try
            {
                var deadline = Soon();
                await arm(deadline);
                _deadline.TrySetResult(deadline);
            }
            catch (Exception e) { _deadline.TrySetException(e); throw; }
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { await Check(); return result; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { await Check(); return result; }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { await Check(); return result; }
    }
}
