using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ArturRios.Cerberus.Data.Configuration;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("CERBERUS_DATA_CONNECTIONSTRING");
        if (PostgresConfiguration.Validate(connection).Failed)
            throw new InvalidOperationException("Invalid or missing CERBERUS_DATA_CONNECTIONSTRING.");
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
    }
}
