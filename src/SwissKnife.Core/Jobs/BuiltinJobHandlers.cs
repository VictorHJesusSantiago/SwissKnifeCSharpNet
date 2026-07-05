using SwissKnife.Core.Repositories;

namespace SwissKnife.Core.Jobs;

/// <summary>Job embutido: expurga recursos soft-deletados além da retenção (também roda via RetentionSweepHostedService periodicamente; aqui fica disponível sob demanda).</summary>
public sealed class PurgeExpiredResourcesJobHandler(ResourceRepository resources) : IJobHandler
{
    public string Kind => "purge-expired-resources";

    public async Task<string?> ExecuteAsync(JobEnvelope job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        progress.Report(10);
        var purged = await resources.PurgeExpiredAsync(cancellationToken);
        progress.Report(100);
        return $"{{\"purged\":{purged}}}";
    }
}
