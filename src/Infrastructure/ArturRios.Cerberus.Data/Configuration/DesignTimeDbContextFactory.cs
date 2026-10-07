using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ArturRios.Cerberus.Data.Configuration;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("CERBERUS_DATA_CONNECTIONSTRING");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("CERBERUS_DATA_CONNECTIONSTRING is required.");
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
    }
}
