namespace SwissKnife.Core.Jobs;

public sealed record JobEnvelope(Guid JobId, Guid TenantId, string Kind, string? PayloadJson);

public interface IJobHandler
{
    string Kind { get; }
    Task<string?> ExecuteAsync(JobEnvelope job, IProgress<int> progress, CancellationToken cancellationToken);
}
