using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Core.Tests;

public sealed class TestDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"swissknife-tests-{Guid.NewGuid():N}.db");
    public TenantContextAccessor TenantAccessor { get; } = new();

    public SwissKnifeDbContext CreateContext()
    {
        var interceptor = new DomainSaveChangesInterceptor(TenantAccessor);
        var options = new DbContextOptionsBuilder<SwissKnifeDbContext>()
            .UseSqlite($"Data Source={_path}")
            .AddInterceptors(interceptor)
            .Options;
        var db = new SwissKnifeDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    public void SetTenant(Guid tenantId, string actor = "tester") =>
        TenantAccessor.Current.Resolve(tenantId, Guid.NewGuid(), ["*"], actor);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
