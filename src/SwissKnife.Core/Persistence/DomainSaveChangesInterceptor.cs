using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Core.Persistence;

/// <summary>
/// Interceptor único de SaveChanges cobrindo:
/// - FND-008: novo ConcurrencyStamp a cada update de Resource.
/// - FND-012: gravação de ResourceHistory imutável a cada create/update/delete de Resource.
/// - FND-040: geração de OutboxMessage na MESMA transação da mudança de domínio.
/// Consolidado em uma única classe por simplicidade; cada responsabilidade é um método
/// isolado e testável.
/// </summary>
public sealed class DomainSaveChangesInterceptor(TenantContextAccessor tenantAccessor) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ApplyAsync(eventData.Context).GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(eventData.Context, cancellationToken);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private async Task ApplyAsync(DbContext? context, CancellationToken cancellationToken = default)
    {
        if (context is null) return;
        var actor = tenantAccessor.Current.ActorName ?? "system";
        var tenantId = tenantAccessor.Current.TenantId;
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries<Resource>().ToArray())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = actor;
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = actor;
                    entry.Entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");
                    var createVersion = await NextVersionAsync(context, entry.Entity.Id, cancellationToken);
                    AddHistory(context, entry, ResourceChangeKind.Create, createVersion);
                    AddOutbox(context, entry.Entity, "resource.created", tenantId, now);
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = actor;
                    entry.Entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");
                    var kind = entry.Entity.IsDeleted && entry.OriginalValues.GetValue<bool>(nameof(Resource.IsDeleted)) is false
                        ? ResourceChangeKind.Delete
                        : ResourceChangeKind.Update;
                    var nextVersion = await NextVersionAsync(context, entry.Entity.Id, cancellationToken);
                    AddHistory(context, entry, kind, nextVersion);
                    AddOutbox(context, entry.Entity, kind == ResourceChangeKind.Delete ? "resource.deleted" : "resource.updated", tenantId, now);
                    break;
            }
        }
    }

    /// <summary>
    /// Próxima versão = maior versão já persistida OU pendente nesta mesma unidade de
    /// trabalho, +1. Consultar o banco (em vez de só o ChangeTracker.Local) é necessário
    /// porque em produção o DbContext tem escopo por requisição — cada requisição só veria
    /// as próprias mudanças em Local, perdendo o histórico de requisições anteriores.
    /// </summary>
    private static async Task<int> NextVersionAsync(DbContext context, Guid resourceId, CancellationToken cancellationToken)
    {
        var persistedMax = await context.Set<ResourceHistory>().AsNoTracking()
            .Where(x => x.ResourceId == resourceId)
            .Select(x => (int?)x.Version)
            .MaxAsync(cancellationToken) ?? 0;

        var localMax = context.Set<ResourceHistory>().Local
            .Where(x => x.ResourceId == resourceId)
            .Select(x => (int?)x.Version)
            .Max() ?? 0;

        return Math.Max(persistedMax, localMax) + 1;
    }

    private static void AddHistory(DbContext context, EntityEntry<Resource> entry, ResourceChangeKind kind, int version)
    {
        context.Set<ResourceHistory>().Add(new ResourceHistory
        {
            ResourceId = entry.Entity.Id,
            Version = version,
            PayloadJsonSnapshot = entry.Entity.PayloadJson,
            Status = entry.Entity.Status,
            ChangedBy = entry.Entity.UpdatedBy,
            ChangedAt = entry.Entity.UpdatedAt,
            ChangeKind = kind
        });
    }

    private static void AddOutbox(DbContext context, Resource resource, string eventType, Guid tenantId, DateTimeOffset now)
    {
        context.Set<OutboxMessageEntity>().Add(new OutboxMessageEntity
        {
            OccurredAt = now,
            EventType = eventType,
            TenantId = tenantId == Guid.Empty ? resource.TenantId : tenantId,
            ResourceId = resource.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                resource.Id,
                resource.Module,
                resource.Name,
                resource.Status,
                resource.TenantId
            })
        });
    }
}
