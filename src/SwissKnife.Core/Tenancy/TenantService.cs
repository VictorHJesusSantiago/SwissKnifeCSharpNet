using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Tenancy;

/// <summary>FND-032/033/034: ciclo de vida de tenants, limites/config e hierarquia organizacional.</summary>
public sealed class TenantService(SwissKnifeDbContext db)
{
    public async Task<Tenant> CreateAsync(string slug, string displayName, CancellationToken cancellationToken = default)
    {
        if (await db.Tenants.AnyAsync(x => x.Slug == slug, cancellationToken))
            throw new ArgumentException($"Já existe um tenant com slug '{slug}'.");
        var tenant = new Tenant { Slug = slug, DisplayName = displayName };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
        return tenant;
    }

    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Tenants.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync(cancellationToken);

    public async Task<bool> SuspendAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await SetStatusAsync(tenantId, TenantStatus.Suspended, cancellationToken);

    public async Task<bool> ReactivateAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await SetStatusAsync(tenantId, TenantStatus.Active, cancellationToken);

    public async Task<bool> DeleteAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await db.Tenants.FindAsync([tenantId], cancellationToken);
        if (tenant is null) return false;
        tenant.Status = TenantStatus.Deleted;
        tenant.DeletedAt = DateTimeOffset.UtcNow;
        foreach (var key in db.ApiKeys.Where(x => x.TenantId == tenantId))
            key.RevokedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> SetStatusAsync(Guid tenantId, TenantStatus status, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FindAsync([tenantId], cancellationToken);
        if (tenant is null) return false;
        tenant.Status = status;
        if (status == TenantStatus.Suspended) tenant.SuspendedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task SetLimitAsync(Guid tenantId, string resourceType, int? maxCount, long? maxStorageBytes, int? maxJobsConcurrent, CancellationToken cancellationToken = default)
    {
        var existing = await db.TenantLimits.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ResourceType == resourceType, cancellationToken);
        if (existing is null)
        {
            db.TenantLimits.Add(new TenantLimit { TenantId = tenantId, ResourceType = resourceType, MaxCount = maxCount, MaxStorageBytes = maxStorageBytes, MaxJobsConcurrent = maxJobsConcurrent });
        }
        else
        {
            existing.MaxCount = maxCount;
            existing.MaxStorageBytes = maxStorageBytes;
            existing.MaxJobsConcurrent = maxJobsConcurrent;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetSettingAsync(Guid tenantId, string key, string value, CancellationToken cancellationToken = default)
    {
        var existing = await db.TenantSettings.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, cancellationToken);
        if (existing is null)
            db.TenantSettings.Add(new TenantSetting { TenantId = tenantId, Key = key, Value = value });
        else
            existing.Value = value;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>FND-027 (suporte a limites): quantidade atual de recursos ativos do tenant para checagem de quota.</summary>
    public async Task<int> CountActiveResourcesAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await db.Resources.CountAsync(x => x.TenantId == tenantId, cancellationToken);

    public async Task<OrgUnit> CreateOrgUnitAsync(Guid tenantId, string name, OrgUnitKind kind, Guid? parentId, CancellationToken cancellationToken = default)
    {
        var unit = new OrgUnit { TenantId = tenantId, Name = name, Kind = kind, ParentId = parentId };
        db.OrgUnits.Add(unit);
        await db.SaveChangesAsync(cancellationToken);
        return unit;
    }

    public async Task<IReadOnlyList<OrgUnit>> ListOrgUnitsAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await db.OrgUnits.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(cancellationToken);
}
