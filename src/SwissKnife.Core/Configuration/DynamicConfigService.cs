using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Configuration;

/// <summary>API-034: configurações dinâmicas com histórico e rollback.</summary>
public sealed class DynamicConfigService(SwissKnifeDbContext db)
{
    public async Task<string?> GetAsync(string key, Guid? tenantId, CancellationToken cancellationToken = default) =>
        (await db.Set<DynamicConfigEntity>().AsNoTracking().FirstOrDefaultAsync(x => x.Key == key && x.TenantId == tenantId, cancellationToken))?.Value;

    public async Task SetAsync(string key, Guid? tenantId, string value, string? actor, CancellationToken cancellationToken = default)
    {
        var existing = await db.Set<DynamicConfigEntity>().FirstOrDefaultAsync(x => x.Key == key && x.TenantId == tenantId, cancellationToken);
        var nextVersion = (existing?.Version ?? 0) + 1;

        db.Set<DynamicConfigHistoryEntry>().Add(new DynamicConfigHistoryEntry
        {
            Key = key, TenantId = tenantId, Value = value, Version = nextVersion, ChangedBy = actor
        });

        if (existing is null)
            db.Set<DynamicConfigEntity>().Add(new DynamicConfigEntity { Key = key, TenantId = tenantId, Value = value, Version = nextVersion, UpdatedBy = actor });
        else
        {
            existing.Value = value;
            existing.Version = nextVersion;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.UpdatedBy = actor;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DynamicConfigHistoryEntry>> HistoryAsync(string key, Guid? tenantId, CancellationToken cancellationToken = default) =>
        await db.Set<DynamicConfigHistoryEntry>().AsNoTracking()
            .Where(x => x.Key == key && x.TenantId == tenantId)
            .OrderByDescending(x => x.Version)
            .ToListAsync(cancellationToken);

    public async Task<bool> RollbackAsync(string key, Guid? tenantId, int version, string? actor, CancellationToken cancellationToken = default)
    {
        var snapshot = await db.Set<DynamicConfigHistoryEntry>().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == key && x.TenantId == tenantId && x.Version == version, cancellationToken);
        if (snapshot is null) return false;
        await SetAsync(key, tenantId, snapshot.Value, actor, cancellationToken);
        return true;
    }
}
