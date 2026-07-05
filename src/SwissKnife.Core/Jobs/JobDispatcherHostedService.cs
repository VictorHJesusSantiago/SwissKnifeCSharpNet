using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Jobs;

/// <summary>Consome a JobQueue e executa o IJobHandler registrado para o Kind do job.</summary>
public sealed class JobDispatcherHostedService(
    JobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<JobDispatcherHostedService> logger) : BackgroundService
{
    // IJobHandler é Scoped (pode depender de serviços como ResourceRepository, que dependem
    // do DbContext); um BackgroundService é Singleton, então os handlers são resolvidos por
    // escopo dentro de RunAsync, nunca injetados diretamente no construtor.
    private readonly Dictionary<Guid, CancellationTokenSource> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueOrphanedJobsAsync(stoppingToken);

        await foreach (var job in queue.DequeueAllAsync(stoppingToken))
        {
            _ = RunAsync(job, stoppingToken);
        }
    }

    /// <summary>Jobs que ficaram "Running" após um crash do processo voltam a "Queued".</summary>
    private async Task RequeueOrphanedJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SwissKnifeDbContext>();
        var orphaned = await db.Jobs.Where(x => x.Status == JobStatus.Running).ToListAsync(cancellationToken);
        foreach (var job in orphaned)
        {
            job.Status = JobStatus.Queued;
            job.StartedAt = null;
        }
        if (orphaned.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var job in orphaned)
                await queue.EnqueueAsync(new JobEnvelope(job.Id, job.TenantId, job.Kind, job.PayloadJson), cancellationToken);
        }
    }

    private async Task RunAsync(JobEnvelope job, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SwissKnifeDbContext>();
        var entity = await db.Jobs.FirstOrDefaultAsync(x => x.Id == job.JobId, stoppingToken);
        if (entity is null) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_running) { _running[job.JobId] = cts; }

        entity.Status = JobStatus.Running;
        entity.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(stoppingToken);

        var progress = new Progress<int>(percent =>
        {
            _ = UpdateProgressAsync(job.JobId, percent, scopeFactory, stoppingToken);
        });

        try
        {
            var handler = scope.ServiceProvider.GetServices<IJobHandler>()
                .FirstOrDefault(x => x.Kind.Equals(job.Kind, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Nenhum handler registrado para o tipo de job '{job.Kind}'.");

            var result = await handler.ExecuteAsync(job, progress, cts.Token);
            entity.Status = JobStatus.Succeeded;
            entity.ResultJson = result;
            entity.ProgressPercent = 100;
        }
        catch (OperationCanceledException)
        {
            entity.Status = JobStatus.Cancelled;
        }
        catch (Exception exception)
        {
            entity.Status = JobStatus.Failed;
            entity.Error = exception.Message;
            logger.LogError(exception, "Job {JobId} ({Kind}) falhou.", job.JobId, job.Kind);
        }
        finally
        {
            entity.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(stoppingToken);
            lock (_running) { _running.Remove(job.JobId); }
        }
    }

    private static async Task UpdateProgressAsync(Guid jobId, int percent, IServiceScopeFactory scopeFactory, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SwissKnifeDbContext>();
        var entity = await db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (entity is null) return;
        entity.ProgressPercent = Math.Clamp(percent, 0, 100);
        await db.SaveChangesAsync(cancellationToken);
    }

    public bool TryCancel(Guid jobId)
    {
        lock (_running)
        {
            if (_running.TryGetValue(jobId, out var cts))
            {
                cts.Cancel();
                return true;
            }
        }
        return false;
    }
}
