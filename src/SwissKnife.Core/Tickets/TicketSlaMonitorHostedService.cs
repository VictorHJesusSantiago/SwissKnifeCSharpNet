using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwissKnife.Core.Auditing;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Eventing;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Tickets;

/// <summary>TKT-010: varre tickets periodicamente e marca violação de SLA de resposta/resolução, publicando um evento e uma entrada de auditoria por violação (base para alertas/escalonamento futuros).</summary>
public sealed class TicketSlaMonitorHostedService(IServiceScopeFactory scopeFactory, IEventBus eventBus, ILogger<TicketSlaMonitorHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Falha ao varrer SLA de tickets.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SwissKnifeDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditLogger>();
        var now = DateTimeOffset.UtcNow;

        var responseBreaches = await db.Tickets
            .Where(x => !x.SlaPaused && !x.SlaResponseBreached && x.FirstRespondedAt == null && x.ResponseDueAt != null && x.ResponseDueAt < now)
            .ToListAsync(cancellationToken);
        foreach (var ticket in responseBreaches)
        {
            ticket.SlaResponseBreached = true;
            await audit.LogAsync(ticket.TenantId, "sla-monitor", "ticket.sla.response_breached", "Ticket", ticket.Id.ToString(), cancellationToken: cancellationToken);
            await eventBus.PublishAsync(new DomainEvent("ticket.sla.response_breached", ticket.TenantId, null, $$"""{"ticketId":"{{ticket.Id}}","number":{{ticket.Number}}}""", now), cancellationToken);
        }

        var resolutionBreaches = await db.Tickets
            .Where(x => !x.SlaPaused && !x.SlaResolutionBreached && x.ResolvedAt == null && x.ResolutionDueAt != null && x.ResolutionDueAt < now)
            .ToListAsync(cancellationToken);
        foreach (var ticket in resolutionBreaches)
        {
            ticket.SlaResolutionBreached = true;
            await audit.LogAsync(ticket.TenantId, "sla-monitor", "ticket.sla.resolution_breached", "Ticket", ticket.Id.ToString(), cancellationToken: cancellationToken);
            await eventBus.PublishAsync(new DomainEvent("ticket.sla.resolution_breached", ticket.TenantId, null, $$"""{"ticketId":"{{ticket.Id}}","number":{{ticket.Number}}}""", now), cancellationToken);
        }

        if (responseBreaches.Count > 0 || resolutionBreaches.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }
}
