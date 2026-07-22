using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Auditing;

/// <summary>API-023: auditoria de login, acesso, exportação e mudança administrativa.</summary>
public sealed class AuditLogger(SwissKnifeDbContext db)
{
    public async Task LogAsync(
        Guid? tenantId, string? actorName, string action, string? targetType = null, string? targetId = null,
        object? details = null, bool success = true, string? ipAddress = null, CancellationToken cancellationToken = default)
    {
        db.Set<AuditLogEntry>().Add(new AuditLogEntry
        {
            TenantId = tenantId,
            ActorName = actorName,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
            Success = success,
            IpAddress = ipAddress
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> QueryAsync(Guid? tenantId, string? action, int take = 100, CancellationToken cancellationToken = default) =>
        await db.Set<AuditLogEntry>().AsNoTracking()
            .Where(x => tenantId == null || x.TenantId == tenantId)
            .Where(x => action == null || x.Action == action)
            .OrderByDescending(x => x.OccurredAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);
}
