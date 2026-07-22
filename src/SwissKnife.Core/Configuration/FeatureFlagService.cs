using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Configuration;

/// <summary>API-033: feature flags globais, por tenant e por ambiente. Precedência: tenant+ambiente > tenant > ambiente > global.</summary>
public sealed class FeatureFlagService(SwissKnifeDbContext db)
{
    public async Task<bool> IsEnabledAsync(string key, Guid? tenantId, string? environment, CancellationToken cancellationToken = default)
    {
        var candidates = await db.Set<FeatureFlagEntity>().AsNoTracking()
            .Where(x => x.Key == key)
            .Where(x => x.TenantId == null || x.TenantId == tenantId)
            .Where(x => x.Environment == null || x.Environment == environment)
            .ToListAsync(cancellationToken);

        var mostSpecific = candidates
            .OrderByDescending(x => (x.TenantId != null ? 2 : 0) + (x.Environment != null ? 1 : 0))
            .FirstOrDefault();

        return mostSpecific?.Enabled ?? false;
    }

    public async Task SetAsync(string key, Guid? tenantId, string? environment, bool enabled, string? actor, CancellationToken cancellationToken = default)
    {
        var existing = await db.Set<FeatureFlagEntity>()
            .FirstOrDefaultAsync(x => x.Key == key && x.TenantId == tenantId && x.Environment == environment, cancellationToken);
        if (existing is null)
            db.Set<FeatureFlagEntity>().Add(new FeatureFlagEntity { Key = key, TenantId = tenantId, Environment = environment, Enabled = enabled, UpdatedBy = actor });
        else
        {
            existing.Enabled = enabled;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.UpdatedBy = actor;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FeatureFlagEntity>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Set<FeatureFlagEntity>().AsNoTracking().ToListAsync(cancellationToken);
}
