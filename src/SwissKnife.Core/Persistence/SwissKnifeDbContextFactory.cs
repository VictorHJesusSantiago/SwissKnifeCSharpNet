using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SwissKnife.Core.Persistence;

/// <summary>Factory usada por "dotnet ef migrations add" em tempo de design.</summary>
public sealed class SwissKnifeDbContextFactory : IDesignTimeDbContextFactory<SwissKnifeDbContext>
{
    public SwissKnifeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SwissKnifeDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new SwissKnifeDbContext(options);
    }
}

public enum DatabaseProvider { Sqlite, PostgreSql, SqlServer }

public static class DatabaseProviderConfigurator
{
    public static void Configure(DbContextOptionsBuilder builder, DatabaseProvider provider, string connectionString)
    {
        switch (provider)
        {
            case DatabaseProvider.Sqlite:
                builder.UseSqlite(connectionString);
                break;
            case DatabaseProvider.PostgreSql:
                builder.UseNpgsql(connectionString);
                break;
            case DatabaseProvider.SqlServer:
                builder.UseSqlServer(connectionString);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }

    public static DatabaseProvider Parse(string? value) => (value ?? "Sqlite").Trim().ToLowerInvariant() switch
    {
        "sqlite" => DatabaseProvider.Sqlite,
        "postgresql" or "postgres" or "npgsql" => DatabaseProvider.PostgreSql,
        "sqlserver" or "mssql" => DatabaseProvider.SqlServer,
        _ => throw new ArgumentException($"Provider de banco desconhecido: {value}")
    };
}
