using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Eventing;

/// <summary>FND-040: despacha OutboxMessages pendentes para o IEventBus in-process, em lote, periodicamente.</summary>
public sealed class OutboxDispatcherHostedService(
    IServiceScopeFactory scopeFactory,
    IEventBus eventBus,
    ILogger<OutboxDispatcherHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Falha ao despachar mensagens do outbox.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SwissKnifeDbContext>();

        var pending = await db.OutboxMessages
            .Where(x => x.ProcessedAt == null)
            .OrderBy(x => x.OccurredAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            try
            {
                await eventBus.PublishAsync(new DomainEvent(message.EventType, message.TenantId, message.ResourceId, message.PayloadJson, message.OccurredAt), cancellationToken);
                message.ProcessedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception exception)
            {
                message.Attempts++;
                message.LastError = exception.Message;
            }
        }
        if (pending.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }
}
