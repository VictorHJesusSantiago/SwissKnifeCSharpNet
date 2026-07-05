using System.Threading.Channels;

namespace SwissKnife.Core.Eventing;

public sealed record DomainEvent(string EventType, Guid TenantId, Guid? ResourceId, string PayloadJson, DateTimeOffset OccurredAt);

/// <summary>
/// FND-040: barramento de eventos in-process. Documentado como decisão consciente —
/// sem broker externo (RabbitMQ/Kafka), os eventos não sobrevivem a um restart do processo
/// além do que já está persistido em OutboxMessages (a durabilidade real vem da tabela,
/// este barramento é só o mecanismo de fan-out para assinantes dentro do processo).
/// </summary>
public interface IEventBus
{
    Task PublishAsync(DomainEvent domainEvent, CancellationToken cancellationToken = default);
    IAsyncEnumerable<DomainEvent> Subscribe(CancellationToken cancellationToken);
}

public sealed class InProcessEventBus : IEventBus
{
    private readonly List<Channel<DomainEvent>> _subscribers = [];
    private readonly Lock _gate = new();

    public Task PublishAsync(DomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        Channel<DomainEvent>[] snapshot;
        lock (_gate) { snapshot = [.. _subscribers]; }
        foreach (var channel in snapshot)
            channel.Writer.TryWrite(domainEvent);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<DomainEvent> Subscribe([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<DomainEvent>();
        lock (_gate) { _subscribers.Add(channel); }
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
                yield return item;
        }
        finally
        {
            lock (_gate) { _subscribers.Remove(channel); }
        }
    }
}
