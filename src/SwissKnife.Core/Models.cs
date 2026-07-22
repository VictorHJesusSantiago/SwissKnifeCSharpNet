using System.Text.Json;

namespace SwissKnife.Core;

public sealed record ModuleDefinition(string Id, string Name, string Surface, string Description);

public static class ModuleCatalog
{
    public static readonly IReadOnlyList<ModuleDefinition> All =
    [
        new("ad-permissions", "Auditor de permissões AD / Azure AD", "API", "Compara permissões concedidas e esperadas."),
        new("tickets", "Sistema de tickets", "API", "Incidentes e solicitações leves."),
        new("snippets", "Snippet manager", "Desktop/API", "Trechos de código pessoais."),
        new("vpn-profiles", "Perfis VPN multi-tenant", "API", "Perfis associados a tenants e grupos."),
        new("multi-cloud", "CLI multi-cloud", "CLI", "Inventário e operações uniformes."),
        new("kubernetes-health", "Saúde Kubernetes", "API", "Resumo de saúde de clusters."),
        new("kubernetes-manifests", "Gerador de manifests", "CLI/API", "Manifests Kubernetes padronizados."),
        new("slow-queries", "Analisador de queries", "API", "Heurísticas e sugestões de índices."),
        new("schema-sync", "Sincronizador de schema", "API", "Diferenças entre schemas."),
        new("api-gateway", "Gateway de API", "API", "Rotas, autenticação e rate limiting."),
        new("webhooks", "Orquestrador de webhooks", "API", "Assinaturas e entregas internas."),
        new("pki", "PKI interna", "API", "Emissão e revogação local."),
        new("ephemeral-environments", "Ambientes efêmeros", "API", "Ciclo de vida de ambientes de teste."),
        new("team-capacity", "Capacidade de equipe", "API", "Alocação e disponibilidade."),
        new("on-call", "Escalonamento on-call", "API", "Escalas e incidentes."),
        new("self-service", "Provisionamento self-service", "API", "Solicitações de ambientes."),
        new("dotnet-profiler", "Profiler .NET", "API", "Métricas de GC, threads e memória."),
        new("disaster-recovery", "Testes de disaster recovery", "API", "Planos e execuções de DR."),
        new("itam", "Gestão de ativos ITAM", "API", "Inventário e ciclo de vida."),
        new("licenses", "Auditoria de licenças", "API", "Instalações e conformidade."),
        new("identity-policies", "Auditoria de MFA e senha", "API", "Conformidade de identidade."),
        new("logs", "Agregador de logs", "API", "Ingestão e consulta simplificadas.")
    ];

    public static bool Exists(string id) => All.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public sealed class ResourceRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Module { get; set; }
    public required string Name { get; set; }
    public string Tenant { get; set; } = "default";
    public string Status { get; set; } = "active";
    public Dictionary<string, string> Data { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record CreateResource(
    string Module,
    string Name,
    string Tenant = "default",
    string Status = "active",
    Dictionary<string, string>? Data = null);

public sealed record PermissionAuditRequest(
    string Principal,
    string Resource,
    IReadOnlyList<string> Assigned,
    IReadOnlyList<string> Required);

public sealed record PermissionAuditResult(
    string Principal,
    string Resource,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Excess,
    bool Compliant);

public sealed record IdentityPolicyRequest(
    string Principal,
    bool MfaEnabled,
    int PasswordLength,
    int PasswordAgeDays,
    bool IsPrivileged);

public sealed record IdentityPolicyResult(bool Compliant, IReadOnlyList<string> Findings);

public sealed record ClusterHealthRequest(
    string Cluster,
    int ReadyNodes,
    int TotalNodes,
    int RunningPods,
    int FailedPods,
    double CpuPercent,
    double MemoryPercent);

public sealed record ClusterHealthResult(string Cluster, int Score, string Status, IReadOnlyList<string> Findings);

public sealed record ManifestRequest(
    string Name,
    string Image,
    int Replicas = 2,
    int Port = 8080,
    string Namespace = "default",
    int CpuMillicores = 250,
    int MemoryMi = 256);

public sealed record QueryAnalysisRequest(string Sql, double DurationMs = 0);
public sealed record QueryAnalysisResult(int RiskScore, IReadOnlyList<string> Findings, IReadOnlyList<string> SuggestedIndexes);

public sealed record SchemaColumn(string Name, string Type, bool Nullable);
public sealed record SchemaTable(string Name, IReadOnlyList<SchemaColumn> Columns);
public sealed record SchemaComparisonRequest(IReadOnlyList<SchemaTable> Source, IReadOnlyList<SchemaTable> Target);
public sealed record SchemaDifference(string Kind, string Object, string Detail);

public sealed record CertificateIssueRequest(
    string CommonName,
    int ValidDays = 365,
    IReadOnlyList<string>? DnsNames = null,
    IReadOnlyList<string>? IpAddresses = null,
    string Profile = "server");
public sealed record IssuedCertificate(string SerialNumber, string CommonName, DateTimeOffset NotAfter, string CertificatePem);

public sealed record LogEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message,
    string Tenant,
    Dictionary<string, string>? Properties = null);

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static readonly JsonSerializerOptions CompactOptions = new(Options)
    {
        WriteIndented = false
    };
}
