using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Schema;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Core.Repositories;

/// <summary>
/// Repositório central de Resource cobrindo FND-001/002/008/009/010/012/013/017/018/024/025.
/// Todo acesso é implicitamente restrito ao tenant do ITenantContext corrente (FND-031).
/// </summary>
public sealed class ResourceRepository(SwissKnifeDbContext db, TenantContextAccessor tenantAccessor)
{
    private Guid TenantId => tenantAccessor.Current.TenantId;

    public async Task<CursorPage<Resource>> ListAsync(
        ResourceFilter filter, string? cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.Resources.AsNoTracking().Where(x => x.TenantId == TenantId);

        if (filter.IncludeDeleted) query = query.IgnoreQueryFilters().Where(x => x.TenantId == TenantId);
        if (filter.Module is not null) query = query.Where(x => x.Module == filter.Module);
        if (filter.Status is not null) query = query.Where(x => x.Status == filter.Status);
        if (filter.OwnerUserId is not null) query = query.Where(x => x.OwnerUserId == filter.OwnerUserId);
        if (filter.Tag is not null) query = query.Where(x => x.Tags.Any(t => t.Tag == filter.Tag));
        if (!string.IsNullOrWhiteSpace(filter.Text))
            query = query.Where(x => x.Name.Contains(filter.Text) || x.PayloadJson.Contains(filter.Text));

        if (TryDecodeCursor(cursor, out var afterUpdatedAt, out var afterId))
            query = query.Where(x => x.UpdatedAt < afterUpdatedAt || (x.UpdatedAt == afterUpdatedAt && x.Id.CompareTo(afterId) < 0));

        var items = await query
            .OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        var next = hasMore ? EncodeCursor(items[^1].UpdatedAt, items[^1].Id) : null;
        return new CursorPage<Resource>(items, next, hasMore);
    }

    public async Task<Resource?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await db.Resources.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == TenantId, cancellationToken);

    public async Task<Resource> CreateAsync(CreateResourceCommand command, CancellationToken cancellationToken = default)
    {
        if (!ModuleCatalog.Exists(command.Module))
            throw new ArgumentException($"Módulo desconhecido: {command.Module}.");
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ArgumentException("Nome é obrigatório.");

        var errors = ModuleSchemaRegistry.Validate(command.Module, command.PayloadJson);
        if (errors.Count > 0) throw new ResourceValidationException(errors);

        var duplicate = await db.Resources.AnyAsync(
            x => x.TenantId == TenantId && x.Module == command.Module && x.Name == command.Name.Trim(),
            cancellationToken);
        if (duplicate) throw new DuplicateResourceNameException(command.Module, command.Name);

        var resource = new Resource
        {
            TenantId = TenantId,
            Module = command.Module,
            Name = command.Name.Trim(),
            Status = string.IsNullOrWhiteSpace(command.Status) ? "active" : command.Status,
            PayloadJson = command.PayloadJson,
            OwnerUserId = command.OwnerUserId,
            TeamOrgUnitId = command.TeamOrgUnitId,
            CostCenter = command.CostCenter
        };
        foreach (var tag in (command.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            resource.Tags.Add(new ResourceTag { ResourceId = resource.Id, Tag = tag });

        db.Resources.Add(resource);
        await db.SaveChangesAsync(cancellationToken);
        return resource;
    }

    public async Task<Resource> UpdateAsync(Guid id, UpdateResourceCommand command, CancellationToken cancellationToken = default)
    {
        var resource = await db.Resources.Include(x => x.Tags)
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == TenantId, cancellationToken)
            ?? throw new KeyNotFoundException($"Recurso {id} não encontrado.");

        if (resource.ConcurrencyStamp != command.ExpectedConcurrencyStamp)
            throw new ConcurrencyConflictException(
                "O recurso foi alterado por outra requisição desde a última leitura (ETag divergente).");

        var errors = ModuleSchemaRegistry.Validate(resource.Module, command.PayloadJson);
        if (errors.Count > 0) throw new ResourceValidationException(errors);

        if (!string.Equals(resource.Status, command.Status, StringComparison.Ordinal))
            await EnsureValidTransitionAsync(resource.Module, resource.Status, command.Status, cancellationToken);

        resource.Name = command.Name.Trim();
        resource.Status = command.Status;
        resource.PayloadJson = command.PayloadJson;
        resource.OwnerUserId = command.OwnerUserId;
        resource.CostCenter = command.CostCenter;

        db.ResourceTags.RemoveRange(resource.Tags);
        resource.Tags.Clear();
        foreach (var tag in (command.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            resource.Tags.Add(new ResourceTag { ResourceId = resource.Id, Tag = tag });

        await db.SaveChangesAsync(cancellationToken);
        return resource;
    }

    private async Task EnsureValidTransitionAsync(string module, string from, string to, CancellationToken cancellationToken)
    {
        var hasAnyRuleForModule = await db.ResourceStateTransitions.AnyAsync(x => x.Module == module, cancellationToken);
        if (!hasAnyRuleForModule) return; // módulo sem regras configuradas: qualquer transição é permitida

        var allowed = await db.ResourceStateTransitions.AnyAsync(
            x => x.Module == module && x.FromState == from && x.ToState == to, cancellationToken);
        if (!allowed) throw new InvalidStateTransitionException(module, from, to);
    }

    public async Task<bool> SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var resource = await db.Resources.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == TenantId, cancellationToken);
        if (resource is null) return false;
        resource.IsDeleted = true;
        resource.DeletedAt = DateTimeOffset.UtcNow;
        resource.DeletedBy = tenantAccessor.Current.ActorName;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<Resource?> RestoreAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var resource = await db.Resources.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == TenantId && x.IsDeleted, cancellationToken);
        if (resource is null) return null;
        resource.IsDeleted = false;
        resource.DeletedAt = null;
        resource.DeletedBy = null;
        await db.SaveChangesAsync(cancellationToken);
        return resource;
    }

    public async Task<IReadOnlyList<Resource>> ListTrashAsync(CancellationToken cancellationToken = default) =>
        await db.Resources.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == TenantId && x.IsDeleted)
            .OrderByDescending(x => x.DeletedAt)
            .ToListAsync(cancellationToken);

    /// <summary>FND-011: expurgo físico de recursos soft-deletados além do prazo de retenção configurado.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var policies = await db.RetentionPolicies.AsNoTracking().ToListAsync(cancellationToken);
        var defaultDays = 30;
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.Resources.IgnoreQueryFilters()
            .Where(x => x.IsDeleted && x.DeletedAt != null)
            .ToListAsync(cancellationToken);

        var purged = 0;
        foreach (var resource in candidates)
        {
            var days = policies.FirstOrDefault(p => p.Module == resource.Module && p.TenantId == resource.TenantId)?.RetainDeletedDays
                ?? policies.FirstOrDefault(p => p.Module == resource.Module && p.TenantId == null)?.RetainDeletedDays
                ?? defaultDays;
            if (resource.DeletedAt!.Value.AddDays(days) <= now)
            {
                db.Resources.Remove(resource);
                purged++;
            }
        }
        if (purged > 0) await db.SaveChangesAsync(cancellationToken);
        return purged;
    }

    public async Task<IReadOnlyList<ResourceHistory>> GetHistoryAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
        await db.ResourceHistories.AsNoTracking()
            .Where(x => x.ResourceId == resourceId)
            .OrderByDescending(x => x.Version)
            .ToListAsync(cancellationToken);

    /// <summary>FND-013: diff textual simples entre dois snapshots de payload (linha a linha do JSON indentado).</summary>
    public async Task<IReadOnlyList<string>> DiffVersionsAsync(Guid resourceId, int fromVersion, int toVersion, CancellationToken cancellationToken = default)
    {
        var snapshots = await db.ResourceHistories.AsNoTracking()
            .Where(x => x.ResourceId == resourceId && (x.Version == fromVersion || x.Version == toVersion))
            .ToDictionaryAsync(x => x.Version, cancellationToken);

        if (!snapshots.TryGetValue(fromVersion, out var from) || !snapshots.TryGetValue(toVersion, out var to))
            throw new KeyNotFoundException("Uma das versões solicitadas não existe.");

        return ComputeLineDiff(from.PayloadJsonSnapshot, to.PayloadJsonSnapshot);
    }

    private static string[] ComputeLineDiff(string fromJson, string toJson)
    {
        string Pretty(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }

        var fromLines = Pretty(fromJson).Split('\n');
        var toLines = Pretty(toJson).Split('\n');
        var result = new List<string>();
        var max = Math.Max(fromLines.Length, toLines.Length);
        for (var i = 0; i < max; i++)
        {
            var a = i < fromLines.Length ? fromLines[i].TrimEnd('\r') : null;
            var b = i < toLines.Length ? toLines[i].TrimEnd('\r') : null;
            if (a == b) continue;
            if (a is not null) result.Add($"- {a}");
            if (b is not null) result.Add($"+ {b}");
        }
        return [.. result];
    }

    /// <summary>FND-013: restaura o payload/estado de uma versão anterior como uma NOVA versão (histórico nunca é reescrito).</summary>
    public async Task<Resource> RestoreVersionAsync(Guid resourceId, int version, CancellationToken cancellationToken = default)
    {
        var resource = await db.Resources.FirstOrDefaultAsync(x => x.Id == resourceId && x.TenantId == TenantId, cancellationToken)
            ?? throw new KeyNotFoundException($"Recurso {resourceId} não encontrado.");
        var snapshot = await db.ResourceHistories.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ResourceId == resourceId && x.Version == version, cancellationToken)
            ?? throw new KeyNotFoundException($"Versão {version} não encontrada.");

        resource.PayloadJson = snapshot.PayloadJsonSnapshot;
        resource.Status = snapshot.Status;
        await db.SaveChangesAsync(cancellationToken);
        return resource;
    }

    private static string EncodeCursor(DateTimeOffset updatedAt, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{updatedAt:O}|{id}"));

    private static bool TryDecodeCursor(string? cursor, out DateTimeOffset updatedAt, out Guid id)
    {
        updatedAt = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = decoded.Split('|');
            if (parts.Length != 2) return false;
            updatedAt = DateTimeOffset.Parse(parts[0]);
            id = Guid.Parse(parts[1]);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
