using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArturRios.Cerberus.Data.Resources;

// Child contexts preserve configured interceptors and dispose neither the
// caller's connection nor transaction. Native mutations use savepoints.
public sealed class TransactionContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly IDbContextFactory<AppDbContext> contexts;
    private readonly DbConnection connection;
    private readonly DbTransaction transaction;
    public TransactionContextFactory(IDbContextFactory<AppDbContext> contexts, AppDbContext db)
    {
        this.contexts = contexts;
        connection = db.Database.GetDbConnection();
        transaction = (db.Database.CurrentTransaction ?? throw new InvalidOperationException("An external transaction is required.")).GetDbTransaction();
    }
    public AppDbContext CreateDbContext()
    {
        var context = contexts.CreateDbContext();
        context.Database.SetDbConnection(connection, contextOwnsConnection: false);
        context.Database.UseTransaction(transaction);
        return context;
    }
}
