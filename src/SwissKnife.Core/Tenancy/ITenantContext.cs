namespace SwissKnife.Core.Tenancy;

/// <summary>
/// Identidade efetiva resolvida a partir da autenticação (nunca do payload da requisição).
/// FND-031: isolamento de dados deriva da identidade, não do corpo da requisição.
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }
    Guid? ApiKeyId { get; }
    IReadOnlyList<string> Scopes { get; }
    string? ActorName { get; }
    bool IsPlatformAdmin { get; }
    bool HasScope(string scope);
}

public sealed class TenantContext : ITenantContext
{
    public static readonly TenantContext Unresolved = new();

    public Guid TenantId { get; private set; }
    public Guid? ApiKeyId { get; private set; }
    public IReadOnlyList<string> Scopes { get; private set; } = [];
    public string? ActorName { get; private set; }
    public bool IsPlatformAdmin => Scopes.Contains("*") || Scopes.Contains("platform:admin");

    public bool HasScope(string scope) => Scopes.Contains("*") || Scopes.Contains(scope);

    public void Resolve(Guid tenantId, Guid apiKeyId, IReadOnlyList<string> scopes, string? actorName)
    {
        TenantId = tenantId;
        ApiKeyId = apiKeyId;
        Scopes = scopes;
        ActorName = actorName;
    }
}

/// <summary>Acessor com escopo de requisição (registrado como Scoped na DI).</summary>
public sealed class TenantContextAccessor
{
    public TenantContext Current { get; } = new();
}
