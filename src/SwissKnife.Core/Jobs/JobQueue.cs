using System.Threading.Channels;

namespace SwissKnife.Core.Jobs;

/// <summary>
/// FND-028/029: fila de jobs assíncronos in-process (Channel&lt;T&gt;). Sem broker externo —
/// jobs "Running" no momento de um crash do processo são reenfileirados na inicialização
/// (ver JobDispatcherHostedService), mas não há garantia exactly-once. Decisão documentada
/// no plano da fundação, aceitável para uso self-hosted single-instance.
/// </summary>
public sealed class JobQueue
{
    private readonly Channel<JobEnvelope> _channel = Channel.CreateUnbounded<JobEnvelope>();

    public ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(job, cancellationToken);

    public IAsyncEnumerable<JobEnvelope> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
