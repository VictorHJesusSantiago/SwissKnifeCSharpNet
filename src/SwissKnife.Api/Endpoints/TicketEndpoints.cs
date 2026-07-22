using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Tenancy;
using SwissKnife.Core.Tickets;

namespace SwissKnife.Api.Endpoints;

public sealed record CreateTicketRequest(
    TicketType Type, string Subject, string? Description,
    TicketPriority Priority = TicketPriority.Medium, TicketImpact Impact = TicketImpact.Medium, TicketUrgency Urgency = TicketUrgency.Medium,
    string? Category = null, string? Subcategory = null, string? RequesterEmail = null,
    Guid? AssigneeUserId = null, Guid? TeamOrgUnitId = null);

public sealed record UpdateTicketRequest(
    string Subject, string? Description, TicketPriority Priority, TicketImpact Impact, TicketUrgency Urgency,
    string? Category, string? Subcategory, Guid? AssigneeUserId, Guid? TeamOrgUnitId);

public sealed record TransitionTicketRequest(string ToStatus);
public sealed record AddTicketCommentRequest(string Body, bool IsInternal = false, bool IsAgentResponse = false);
public sealed record LinkTicketRequest(Guid TargetTicketId, TicketRelationType Type);
public sealed record SetTicketSlaPolicyRequest(Guid? TenantId, TicketPriority Priority, int ResponseMinutes, int ResolutionMinutes);

/// <summary>TKT-001..025: endpoints do módulo de Tickets.</summary>
public static class TicketEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var tickets = api.MapGroup("/tickets");

        tickets.MapPost("/", async (CreateTicketRequest request, TicketService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(new CreateTicketCommand(
                request.Type, request.Subject, request.Description, request.Priority, request.Impact, request.Urgency,
                request.Category, request.Subcategory, request.RequesterEmail, request.AssigneeUserId, request.TeamOrgUnitId), ct);
            return Results.Created($"/api/tickets/{created.Id}", ToResponse(created));
        });

        tickets.MapGet("/", async (TicketType? type, string? status, TicketPriority? priority, Guid? assigneeUserId, bool? breachedOnly, TicketService service, CancellationToken ct) =>
        {
            var items = await service.ListAsync(new TicketFilter(type, status, priority, assigneeUserId, breachedOnly ?? false), cancellationToken: ct);
            return Results.Ok(items.Select(ToResponse));
        });

        tickets.MapGet("/metrics", async (TicketService service, CancellationToken ct) => Results.Ok(await service.GetMetricsAsync(ct)));

        tickets.MapGet("/by-number/{number:int}", async (int number, TicketService service, CancellationToken ct) =>
            await service.GetByNumberAsync(number, ct) is { } ticket ? Results.Ok(ToResponse(ticket)) : Results.NotFound());

        tickets.MapGet("/{id:guid}", async (Guid id, TicketService service, HttpContext http, CancellationToken ct) =>
        {
            var ticket = await service.GetAsync(id, ct);
            if (ticket is null) return Results.NotFound();
            http.Response.Headers.ETag = $"\"{ticket.ConcurrencyStamp}\"";
            return Results.Ok(ToResponse(ticket));
        });

        tickets.MapPut("/{id:guid}", async (Guid id, UpdateTicketRequest request, HttpRequest http, TicketService service, CancellationToken ct) =>
        {
            var etag = http.Headers.IfMatch.FirstOrDefault()?.Trim('"')
                ?? (await service.GetAsync(id, ct))?.ConcurrencyStamp
                ?? throw new KeyNotFoundException($"Ticket {id} não encontrado.");
            var updated = await service.UpdateFieldsAsync(id, new UpdateTicketFieldsCommand(
                request.Subject, request.Description, request.Priority, request.Impact, request.Urgency,
                request.Category, request.Subcategory, request.AssigneeUserId, request.TeamOrgUnitId, etag), ct);
            return Results.Ok(ToResponse(updated));
        });

        tickets.MapPost("/{id:guid}/transition", async (Guid id, TransitionTicketRequest request, HttpRequest http, TicketService service, CancellationToken ct) =>
        {
            var etag = http.Headers.IfMatch.FirstOrDefault()?.Trim('"')
                ?? (await service.GetAsync(id, ct))?.ConcurrencyStamp
                ?? throw new KeyNotFoundException($"Ticket {id} não encontrado.");
            var updated = await service.TransitionStatusAsync(id, request.ToStatus, etag, ct);
            return Results.Ok(ToResponse(updated));
        });

        tickets.MapPost("/{id:guid}/comments", async (Guid id, AddTicketCommentRequest request, TicketService service, CancellationToken ct) =>
            Results.Created($"/api/tickets/{id}/comments", await service.AddCommentAsync(id, request.Body, request.IsInternal, request.IsAgentResponse, ct)));
        tickets.MapGet("/{id:guid}/comments", async (Guid id, TicketService service, CancellationToken ct) =>
            Results.Ok(await service.ListCommentsAsync(id, ct)));

        tickets.MapPost("/{id:guid}/links", async (Guid id, LinkTicketRequest request, TicketService service, CancellationToken ct) =>
            Results.Created($"/api/tickets/{id}/links", await service.LinkAsync(id, request.TargetTicketId, request.Type, ct)));
        tickets.MapGet("/{id:guid}/links", async (Guid id, TicketService service, CancellationToken ct) =>
            Results.Ok(await service.ListRelationshipsAsync(id, ct)));

        var slaPolicies = tickets.MapGroup("/sla-policies").AddEndpointFilter<PlatformAdminFilter>();
        slaPolicies.MapGet("/", async (SwissKnifeDbContext db, ITenantContext tenant, CancellationToken ct) =>
            Results.Ok(await db.TicketSlaPolicies.AsNoTracking().Where(x => x.TenantId == tenant.TenantId || x.TenantId == null).ToListAsync(ct)));
        slaPolicies.MapPost("/", async (SetTicketSlaPolicyRequest request, SwissKnifeDbContext db, CancellationToken ct) =>
        {
            var existing = await db.TicketSlaPolicies.FirstOrDefaultAsync(x => x.TenantId == request.TenantId && x.Priority == request.Priority, ct);
            if (existing is null)
                db.TicketSlaPolicies.Add(new TicketSlaPolicy { TenantId = request.TenantId, Priority = request.Priority, ResponseMinutes = request.ResponseMinutes, ResolutionMinutes = request.ResolutionMinutes });
            else
            {
                existing.ResponseMinutes = request.ResponseMinutes;
                existing.ResolutionMinutes = request.ResolutionMinutes;
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static object ToResponse(TicketEntity ticket) => new
    {
        ticket.Id,
        ticket.Number,
        ticket.Type,
        ticket.Subject,
        ticket.Description,
        ticket.Status,
        ticket.Priority,
        ticket.Impact,
        ticket.Urgency,
        ticket.Category,
        ticket.Subcategory,
        ticket.AssigneeUserId,
        ticket.TeamOrgUnitId,
        ticket.RequesterEmail,
        ticket.ResponseDueAt,
        ticket.ResolutionDueAt,
        ticket.FirstRespondedAt,
        ticket.ResolvedAt,
        ticket.ClosedAt,
        ticket.SlaResponseBreached,
        ticket.SlaResolutionBreached,
        ticket.ReopenedCount,
        ticket.CreatedAt,
        ticket.UpdatedAt,
        ETag = ticket.ConcurrencyStamp
    };
}
