using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SwissKnife.Api;
using SwissKnife.Api.Endpoints;
using SwissKnife.Core;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Tenancy;

var builder = WebApplication.CreateBuilder(args);

// IMPORTANTE: nenhum caminho de dados é lido de builder.Configuration aqui, antes do
// Build(). O WebApplicationFactory usado pelos testes de integração só injeta overrides de
// configuração no momento do Build() (via um host builder adiado); ler valores antes disso
// captura um snapshot que ignora esses overrides. Por isso os diretórios ficam em
// SwissKnifePaths, resolvido lazily pela DI (sempre depois do Build()).
builder.Services.AddSwissKnifeCore();
builder.Services.AddSingleton(sp => new LogStore(Path.Combine(sp.GetRequiredService<SwissKnife.Core.SwissKnifePaths>().DataDirectory, "logs.ndjson")));

// O rate limiter roda ANTES da autenticação (protege também endpoints anônimos), então
// ainda não há ITenantContext resolvido neste ponto do pipeline; a partição usa o header
// X-Api-Key como aproximação (ainda não validado) com fallback por IP. O isolamento de
// dados por tenant em si é sempre imposto depois, pelo TenantResolutionMiddleware.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Request.Headers["X-Api-Key"].FirstOrDefault() ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = context.RequestServices.GetRequiredService<IConfiguration>().GetValue("SwissKnife:RequestsPerMinute", 120),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SwissKnifeDbContext>();
    await db.Database.MigrateAsync();
}

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapGet("/", () => Results.Ok(new
{
    name = "SwissKnife",
    version = "1.0.0",
    modules = ModuleCatalog.All.Count,
    documentation = "/api/modules",
    health = "/health"
})).AllowAnonymous();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow })).AllowAnonymous();

var api = app.MapGroup("/api");
api.MapGet("/modules", () => ModuleCatalog.All);

ResourceEndpoints.Map(api);
PlatformEndpoints.Map(api);

api.MapPost("/ad/permissions/audit", (PermissionAuditRequest request) =>
    Results.Ok(AnalysisServices.AuditPermissions(request)));
api.MapPost("/identity/policies/audit", (IdentityPolicyRequest request) =>
    Results.Ok(AnalysisServices.AuditIdentity(request)));
api.MapPost("/kubernetes/health", (ClusterHealthRequest request) =>
    Results.Ok(AnalysisServices.AnalyzeCluster(request)));
api.MapPost("/kubernetes/manifests", (ManifestRequest request) =>
    Results.Text(AnalysisServices.GenerateManifest(request), "application/yaml"));
api.MapPost("/database/queries/analyze", (QueryAnalysisRequest request) =>
    Results.Ok(AnalysisServices.AnalyzeQuery(request)));
api.MapPost("/database/schemas/compare", (SchemaComparisonRequest request) =>
    Results.Ok(AnalysisServices.CompareSchemas(request)));

api.MapPost("/pki/certificates", async (CertificateIssueRequest request, SwissKnife.Core.Repositories.ResourceRepository resources, CancellationToken ct) =>
{
    var certificate = AnalysisServices.IssueCertificate(request);
    await resources.CreateAsync(new(
        "pki",
        request.CommonName,
        "issued",
        System.Text.Json.JsonSerializer.Serialize(new { serialNumber = certificate.SerialNumber, notAfter = certificate.NotAfter.ToString("O") })), ct);
    return Results.Created($"/api/pki/certificates/{certificate.SerialNumber}", certificate);
});
api.MapPost("/pki/certificates/{serial}/revoke", async (string serial, SwissKnife.Core.Repositories.ResourceRepository resources, CancellationToken ct) =>
{
    var revocation = await resources.CreateAsync(new(
        "pki",
        serial,
        "revoked",
        System.Text.Json.JsonSerializer.Serialize(new { revokedAt = DateTimeOffset.UtcNow.ToString("O") })), ct);
    return Results.Ok(revocation);
});
api.MapGet("/dotnet/profiler/snapshot", AnalysisServices.GetRuntimeMetrics);

api.MapPost("/logs", async (LogInput input, LogStore store, CancellationToken ct) =>
{
    var entry = new LogEntry(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        input.Level,
        input.Source,
        input.Message,
        input.Tenant,
        input.Properties);
    return Results.Created($"/api/logs/{entry.Id}", await store.AddAsync(entry, ct));
});
api.MapGet("/logs", async (
    string? level,
    string? source,
    string? text,
    string? tenant,
    int? take,
    LogStore store,
    CancellationToken ct) =>
    Results.Ok(await store.QueryAsync(level, source, text, tenant, take ?? 100, ct)));

app.Run();

public partial class Program;
