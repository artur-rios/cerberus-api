using System.Runtime.CompilerServices;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ArturRios.Cerberus.Data.Accounts.Export;

internal sealed class AccountExportLease(AppDbContext db, IDbContextTransaction transaction, AccountExportRequest request) : IAccountExportLease
{
    private List<AccountExportRow> _batch = [];
    private string _capturedHeader = "";
    private int _consumed;
    private int _disposed;
    public AccountExportHeader Header { get; private set; } = null!;
    internal async Task InitializeAsync(CancellationToken ct)
    {
        _batch = await Fetch(ct);
        if (_batch.Count == 0)
            throw AccountExportRow.Unavailable();
        var first = _batch[0];
        if (first.Error is not null)
            throw new AccountExportException(first.Error is "not_found" or "vault_access_denied" or "fresh_authentication_required" or "revision_conflict" ? first.Error : "persistence_unavailable");
        _capturedHeader = first.Header ?? throw AccountExportRow.Unavailable();
        Header = JsonSerializer.Deserialize<AccountExportHeader>(_capturedHeader, AccountExportRow.s_json) ?? throw AccountExportRow.Unavailable();
        if (!Header.IsValid(request.Actor, request.ExpectedRevision))
            throw AccountExportRow.Unavailable();
        await Fence(ct);
    }
    public async IAsyncEnumerable<AccountExportEntry> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0 || Volatile.Read(ref _disposed) != 0)
            throw AccountExportRow.Unavailable();
        try
        {
            while (_batch.Count > 0)
            {
                await Fence(cancellationToken);
                foreach (var row in _batch)
                { cancellationToken.ThrowIfCancellationRequested(); yield return row.Read(Header, _capturedHeader); }
                _batch = await Fetch(cancellationToken);
            }
            await Fence(cancellationToken);
        }
        finally { await DisposeAsync(); }
    }
    private Task<List<AccountExportRow>> Fetch(CancellationToken ct) => db.Database.SqlQueryRaw<AccountExportRow>(AccountExportSql.Fetch).ToListAsync(ct);
    private async Task Fence(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var pending = new HashSet<(string Kind, Guid Id)>();
        foreach (var row in _batch)
        { foreach (var id in AccountExportRow.Ids(row.Read(Header, _capturedHeader))) pending.Add(id); foreach (var id in row.Dependencies()) pending.Add(id); }
        var ids = pending.Select(id => new { kind = id.Kind, id = id.Id });
        var parameters = AccountExportSql.Parameters(request, false).Concat([
            new NpgsqlParameter("owner",Header.AccountId),new NpgsqlParameter("policy",Header.PolicyRevision),
            new NpgsqlParameter("generation",Header.RevocationGeneration),new NpgsqlParameter("pending",JsonSerializer.Serialize(ids))]).ToArray();
        var results = await db.Database.SqlQueryRaw<string?>(AccountExportSql.Fence, parameters).ToListAsync(ct);
        if (results.Count != 1)
            throw AccountExportRow.Unavailable();
        if (results[0] is { } error)
            throw new AccountExportException(error);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _batch = [];
        try
        { await transaction.DisposeAsync(); }
        finally { await db.DisposeAsync(); }
    }
}
