using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Jobs;

/// <summary>
/// FND-030: agendador interno recorrente com fuso horário, via expressões cron (Cronos).
/// Sem coordenação entre múltiplas instâncias da API — risco de execução duplicada se
/// escalado horizontalmente (decisão documentada, aceitável para uso single-instance).
/// </summary>
public sealed class ScheduledJobRunnerHostedService(IServiceScopeFactory scopeFactory, JobQueue queue, ILogger<ScheduledJobRunnerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Falha ao avaliar agendamentos.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SwissKnifeDbContext>();
        var now = DateTimeOffset.UtcNow;

        var schedules = await db.ScheduledJobs.Where(x => x.Enabled).ToListAsync(cancellationToken);
        foreach (var schedule in schedules)
        {
            var expression = CronExpression.Parse(schedule.CronExpression);
            var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
            schedule.NextRunAt ??= expression.GetNextOccurrence(now.UtcDateTime, zone);

            if (schedule.NextRunAt is not null && schedule.NextRunAt <= now)
            {
                var job = new JobEntity
                {
                    TenantId = schedule.TenantId ?? Guid.Empty,
                    Kind = schedule.Kind,
                    PayloadJson = schedule.PayloadJson
                };
                db.Jobs.Add(job);
                schedule.LastRunAt = now;
                schedule.NextRunAt = expression.GetNextOccurrence(now.UtcDateTime, zone);
                await db.SaveChangesAsync(cancellationToken);
                await queue.EnqueueAsync(new JobEnvelope(job.Id, job.TenantId, job.Kind, job.PayloadJson), cancellationToken);
            }
        }
    }
}
