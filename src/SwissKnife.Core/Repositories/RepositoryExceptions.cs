namespace SwissKnife.Core.Repositories;

public sealed class ConcurrencyConflictException(string message) : Exception(message);

public sealed class DuplicateResourceNameException(string module, string name)
    : Exception($"Já existe um recurso ativo chamado '{name}' no módulo '{module}' para este tenant.");

public sealed class InvalidStateTransitionException(string module, string from, string to)
    : Exception($"Transição de estado inválida em '{module}': '{from}' -> '{to}'.");

public sealed class ResourceValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
