using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Repositories;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Core.Tickets;

/// <summary>
/// TKT-001..025: serviço de domínio de tickets. Cobre numeração sequencial (TKT-002),
/// SLA de resposta/resolução (TKT-008/009), transições de estado reaproveitando
/// ResourceStateTransitions com Module="tickets" (TKT-007), reabertura (TKT-025) e
/// vínculos entre tickets (TKT-013).
/// </summary>
public sealed class TicketService(SwissKnifeDbContext db, TenantContextAccessor tenantAccessor)
{
    private const string TicketModule = "tickets";
    private Guid TenantId => tenantAccessor.Current.TenantId;
    private string Actor => tenantAccessor.Current.ActorName ?? "system";

    // TKT-008: padrões usados quando não há política de SLA configurada para o tenant/prioridade.
    private static readonly Dictionary<TicketPriority, (int ResponseMinutes, int ResolutionMinutes)> DefaultSla = new()
    {
        [TicketPriority.Critical] = (15, 240),
        [TicketPriority.High] = (30, 480),
        [TicketPriority.Medium] = (120, 1440),
        [TicketPriority.Low] = (480, 4320)
    };

    public async Task<TicketEntity> CreateAsync(CreateTicketCommand command, CancellationToken cancellationToken = default)
    {
        var number = await NextNumberAsync(cancellationToken);
        var (responseMinutes, resolutionMinutes) = await ResolveSlaAsync(command.Priority, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var ticket = new TicketEntity
        {
            TenantId = TenantId,
            Number = number,
            Type = command.Type,
            Subject = command.Subject,
            Description = command.Description,
            Priority = command.Priority,
            Impact = command.Impact,
            Urgency = command.Urgency,
            Category = command.Category,
            Subcategory = command.Subcategory,
            RequesterEmail = command.RequesterEmail,
            AssigneeUserId = command.AssigneeUserId,
            TeamOrgUnitId = command.TeamOrgUnitId,
            Status = "new",
            ResponseDueAt = now.AddMinutes(responseMinutes),
            ResolutionDueAt = now.AddMinutes(resolutionMinutes),
            CreatedBy = Actor,
            UpdatedBy = Actor
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(cancellationToken);
        return ticket;
    }

    private async Task<int> NextNumberAsync(CancellationToken cancellationToken)
    {
        var sequence = await db.TicketNumberSequences.FirstOrDefaultAsync(x => x.TenantId == TenantId, cancellationToken);
        if (sequence is null)
        {
            sequence = new TicketNumberSequence { TenantId = TenantId, LastNumber = 0 };
            db.TicketNumberSequences.Add(sequence);
        }
        sequence.LastNumber++;
        await db.SaveChangesAsync(cancellationToken);
        return sequence.LastNumber;
    }

    private async Task<(int ResponseMinutes, int ResolutionMinutes)> ResolveSlaAsync(TicketPriority priority, CancellationToken cancellationToken)
    {
        var policy = await db.TicketSlaPolicies.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Priority == priority && (x.TenantId == TenantId || x.TenantId == null), cancellationToken);
        if (policy is not null) return (policy.ResponseMinutes, policy.ResolutionMinutes);
        var fallback = DefaultSla[priority];
        return fallback;
    }

    public async Task<TicketEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await db.Tickets.Include(x => x.Watchers).FirstOrDefaultAsync(x => x.Id == id && x.TenantId == TenantId, cancellationToken);

    public async Task<TicketEntity?> GetByNumberAsync(int number, CancellationToken cancellationToken = default) =>
        await db.Tickets.FirstOrDefaultAsync(x => x.Number == number && x.TenantId == TenantId, cancellationToken);

    public async Task<IReadOnlyList<TicketEntity>> ListAsync(TicketFilter filter, int take = 50, CancellationToken cancellationToken = default)
    {
        var query = db.Tickets.AsNoTracking().Where(x => x.TenantId == TenantId);
        if (filter.Type is not null) query = query.Where(x => x.Type == filter.Type);
        if (filter.Status is not null) query = query.Where(x => x.Status == filter.Status);
        if (filter.Priority is not null) query = query.Where(x => x.Priority == filter.Priority);
        if (filter.AssigneeUserId is not null) query = query.Where(x => x.AssigneeUserId == filter.AssigneeUserId);
        if (filter.IncludeBreachedOnly) query = query.Where(x => x.SlaResponseBreached || x.SlaResolutionBreached);
        return await query.OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(cancellationToken);
    }

    public async Task<TicketEntity> UpdateFieldsAsync(Guid id, UpdateTicketFieldsCommand command, CancellationToken cancellationToken = default)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == TenantId, cancellationToken)
            ?? throw new KeyNotFoundException($"Ticket {id} não encontrado.");
        if (ticket.ConcurrencyStamp != command.ExpectedConcurrencyStamp)
            throw new ConcurrencyConflictException("O ticket foi alterado por outra requisição desde a última leitura (ETag divergente).");

        ticket.Subject = command.Subject;
        ticket.Description = command.Description;
        ticket.Priority = command.Priority;
        ticket.Impact = command.Impact;
        ticket.Urgency = command.Urgency;
        ticket.Category = command.Category;
        ticket.Subcategory = command.Subcategory;
        ticket.AssigneeUserId = command.AssigneeUserId;
        ticket.TeamOrgUnitId = command.TeamOrgUnitId;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        ticket.UpdatedBy = Actor;
        ticket.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(cancellationToken);
        return ticket;
    }

    /// <summary>TKT-007: transição de estado validada contra ResourceStateTransitions (Module="tickets").</summary>
    public async Task<TicketEntity> TransitionStatusAsync(Guid id, string toStatus, string expectedConcurrencyStamp, CancellationToken cancellationToken = default)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == TenantId, cancellationToken)
            ?? throw new KeyNotFoundException($"Ticket {id} não encontrado.");
        if (ticket.ConcurrencyStamp != expectedConcurrencyStamp)
            throw new ConcurrencyConflictException("O ticket foi alterado por outra requisição desde a última leitura (ETag divergente).");

        var hasRules = await db.ResourceStateTransitions.AnyAsync(x => x.Module == TicketModule, cancellationToken);
        if (hasRules)
        {
            var allowed = await db.ResourceStateTransitions.AnyAsync(
                x => x.Module == TicketModule && x.FromState == ticket.Status && x.ToState == toStatus, cancellationToken);
            if (!allowed) throw new InvalidStateTransitionException(TicketModule, ticket.Status, toStatus);
        }

        var wasResolvedOrClosed = ticket.Status is "resolved" or "closed";
        ticket.Status = toStatus;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        ticket.UpdatedBy = Actor;
        ticket.ConcurrencyStamp = Guid.NewGuid().ToString("N");

        if (toStatus == "resolved") ticket.ResolvedAt = DateTimeOffset.UtcNow;
        if (toStatus == "closed") ticket.ClosedAt = DateTimeOffset.UtcNow;

        // TKT-025: reabrir um ticket já resolvido/fechado conta como reabertura — sinal de
        // qualidade de atendimento reportado nas métricas.
        if (wasResolvedOrClosed && toStatus is not ("resolved" or "closed"))
        {
            ticket.ReopenedCount++;
            ticket.ResolvedAt = null;
            ticket.ClosedAt = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        return ticket;
    }

    /// <summary>TKT-005/018: comentário público ou nota interna; o primeiro comentário do agente marca a primeira resposta (SLA).</summary>
    public async Task<TicketComment> AddCommentAsync(Guid ticketId, string body, bool isInternal, bool isAgentResponse, CancellationToken cancellationToken = default)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == TenantId, cancellationToken)
            ?? throw new KeyNotFoundException($"Ticket {ticketId} não encontrado.");

        var comment = new TicketComment { TicketId = ticketId, AuthorName = Actor, Body = body, IsInternal = isInternal };
        db.TicketComments.Add(comment);

        if (isAgentResponse && ticket.FirstRespondedAt is null)
            ticket.FirstRespondedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return comment;
    }

    public async Task<IReadOnlyList<TicketComment>> ListCommentsAsync(Guid ticketId, CancellationToken cancellationToken = default) =>
        await db.TicketComments.AsNoTracking().Where(x => x.TicketId == ticketId).OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken);

    /// <summary>TKT-013: vincula dois tickets (pai/filho, duplicado, bloqueador, relacionado).</summary>
    public async Task<TicketRelationship> LinkAsync(Guid sourceId, Guid targetId, TicketRelationType type, CancellationToken cancellationToken = default)
    {
        var relationship = new TicketRelationship { SourceTicketId = sourceId, TargetTicketId = targetId, Type = type };
        db.TicketRelationships.Add(relationship);
        await db.SaveChangesAsync(cancellationToken);
        return relationship;
    }

    public async Task<IReadOnlyList<TicketRelationship>> ListRelationshipsAsync(Guid ticketId, CancellationToken cancellationToken = default) =>
        await db.TicketRelationships.AsNoTracking()
            .Where(x => x.SourceTicketId == ticketId || x.TargetTicketId == ticketId)
            .ToListAsync(cancellationToken);

    /// <summary>TKT-025: métricas de volume, backlog, aging, SLA, reabertura e resolução.</summary>
    public async Task<TicketMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        var all = await db.Tickets.AsNoTracking().Where(x => x.TenantId == TenantId).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var open = all.Where(x => x.Status is not ("resolved" or "closed")).ToArray();
        var resolved = all.Where(x => x.ResolvedAt is not null).ToArray();
        var closed = all.Where(x => x.Status == "closed").ToArray();

        var avgAgingOpen = open.Length == 0 ? 0 : open.Average(x => (now - x.CreatedAt).TotalHours);
        var avgResolutionHours = resolved.Length == 0 ? 0 : resolved.Average(x => (x.ResolvedAt!.Value - x.CreatedAt).TotalHours);

        var respondedTickets = all.Where(x => x.FirstRespondedAt is not null || x.SlaResponseBreached).ToArray();
        var responseCompliance = respondedTickets.Length == 0 ? 1.0 : respondedTickets.Count(x => !x.SlaResponseBreached) / (double)respondedTickets.Length;

        var resolutionTracked = all.Where(x => x.ResolvedAt is not null || x.SlaResolutionBreached).ToArray();
        var resolutionCompliance = resolutionTracked.Length == 0 ? 1.0 : resolutionTracked.Count(x => !x.SlaResolutionBreached) / (double)resolutionTracked.Length;

        var reopenRate = all.Count == 0 ? 0 : all.Count(x => x.ReopenedCount > 0) / (double)all.Count;

        var volumeByPriority = all.GroupBy(x => x.Priority.ToString()).ToDictionary(g => g.Key, g => g.Count());

        return new TicketMetrics(open.Length, resolved.Length, closed.Length, avgAgingOpen, avgResolutionHours, responseCompliance, resolutionCompliance, reopenRate, volumeByPriority);
    }
}
