using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Jobs;

/// <summary>FND-011: expurgo periódico de recursos soft-deletados além da retenção configurada, por tenant.</summary>
public sealed class RetentionSweepHostedService(IServiceScopeFactory scopeFactory, ILogger<RetentionSweepHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Falha no expurgo de retenção.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SwissKnifeDbContext>();
        var policies = await db.RetentionPolicies.AsNoTracking().ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.Resources.IgnoreQueryFilters()
            .Where(x => x.IsDeleted && x.DeletedAt != null)
            .ToListAsync(cancellationToken);

        var purged = 0;
        foreach (var resource in candidates)
        {
            var days = policies.FirstOrDefault(p => p.Module == resource.Module && p.TenantId == resource.TenantId)?.RetainDeletedDays
                ?? policies.FirstOrDefault(p => p.Module == resource.Module && p.TenantId == null)?.RetainDeletedDays
                ?? 30;
            if (resource.DeletedAt!.Value.AddDays(days) <= now)
            {
                db.Resources.Remove(resource);
                purged++;
            }
        }
        if (purged > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Expurgo de retenção removeu {Count} recurso(s).", purged);
        }
    }
}
