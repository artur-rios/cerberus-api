using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArturRios.Cerberus.Data.Resources;

// Child contexts preserve configured interceptors and dispose neither the
// caller's connection nor transaction. Native mutations use savepoints.
public sealed class TransactionContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly IDbContextFactory<AppDbContext> _contexts;
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    public TransactionContextFactory(IDbContextFactory<AppDbContext> contexts, AppDbContext db)
    {
        this._contexts = contexts;
        _connection = db.Database.GetDbConnection();
        _transaction = (db.Database.CurrentTransaction ?? throw new InvalidOperationException("An external transaction is required.")).GetDbTransaction();
    }
    public AppDbContext CreateDbContext()
    {
        var context = _contexts.CreateDbContext();
        context.Database.SetDbConnection(_connection, contextOwnsConnection: false);
        context.Database.UseTransaction(_transaction);
        return context;
    }
}
