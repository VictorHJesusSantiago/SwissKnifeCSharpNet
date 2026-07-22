using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwissKnife.Api;
using SwissKnife.Api.Endpoints;
using SwissKnife.Core;
using SwissKnife.Core.Auth;
using SwissKnife.Core.Configuration;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Tenancy;

var builder = WebApplication.CreateBuilder(args);

// O provider EventLog adicionado automaticamente no Windows exige permissões que não
// existem em hosts de teste, containers e execuções non-admin. Console/Debug mantêm os
// diagnósticos disponíveis e tornam o host portátil.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// IMPORTANTE: nenhum caminho de dados/config é lido de builder.Configuration aqui, antes do
// Build(). O WebApplicationFactory usado pelos testes de integração só injeta overrides de
// configuração no momento do Build() (via um host builder adiado); ler valores antes disso
// captura um snapshot que ignora esses overrides. Por isso tudo é resolvido lazily pela DI
// (sempre depois do Build()) — ver SwissKnifePaths e JwtBearerOptionsConfigurator.
builder.Services.AddSwissKnifeCore();
builder.Services.AddSingleton(sp => new LogStore(Path.Combine(sp.GetRequiredService<SwissKnife.Core.SwissKnifePaths>().DataDirectory, "logs.ndjson")));

// Enums trafegam como string ("Critical", "Incident") em vez de índice numérico — mais legível
// no corpo da requisição/resposta e no schema OpenAPI, e evita quebrar silenciosamente a API
// ao reordenar valores de um enum no futuro.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// API-010/012/014: autenticação JWT Bearer para sessões de usuário local, como alternativa
// à X-Api-Key de serviço-a-serviço (ambas resolvidas pelo mesmo ApiKeyMiddleware).
// OptionsFactory<T> só enxerga configuradores registrados como IConfigureOptions<T> (mesmo
// quando a classe também implementa IConfigureNamedOptions<T> para o overload com nome de
// esquema) — registrar só como IConfigureNamedOptions<T> faz o Configure() nunca ser chamado.
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, JwtBearerOptionsConfigurator>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddAuthorization();
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 10 * 1024 * 1024;
    options.ValueLengthLimit = 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 10 * 1024 * 1024);

// API-001/002: OpenAPI + Swagger UI (autenticado — só habilitado quando a própria API key/JWT válido é fornecido, como qualquer outra rota).
builder.Services.AddOpenApi();

// API-009: CORS configurável por ambiente (origens vêm de configuração, nunca "*" fora de Development).
builder.Services.AddCors(options =>
{
    options.AddPolicy("swissknife", policyBuilder =>
    {
        var origins = builder.Configuration.GetSection("SwissKnife:Cors:AllowedOrigins").Get<string[]>();
        if (origins is { Length: > 0 })
            policyBuilder.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
        else if (builder.Environment.IsDevelopment())
            policyBuilder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

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

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseResponseCompression();
app.UseCors("swissknife");
app.UseRateLimiter();

// API-001/002: OpenAPI/Swagger ficam ANTES do ApiKeyMiddleware de propósito — documentação
// não deve exigir credencial para ser consultada em ambiente de desenvolvimento (mesma
// rede/host); os dados de negócio continuam protegidos normalmente.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "SwissKnife API v1"));
}

app.UseMiddleware<MaintenanceModeMiddleware>();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapGet("/", () => Results.Ok(new
{
    name = "SwissKnife",
    version = "1.0.0",
    modules = ModuleCatalog.All.Count,
    documentation = "/api/modules",
    health = "/health/live"
})).AllowAnonymous();

// API-030: liveness (o processo está de pé) vs. readiness (dependências, ex. banco, respondem).
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow })).AllowAnonymous();
app.MapGet("/health/ready", async (SwissKnifeDbContext db, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    return canConnect
        ? Results.Ok(new { status = "ready", timestamp = DateTimeOffset.UtcNow })
        : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow })).AllowAnonymous(); // mantido por compatibilidade

// API-003: versionamento simples — o mesmo conjunto de rotas é exposto em /api (alias
// não-versionado, para não quebrar integrações existentes) e em /api/v1 (canônico).
void MapAllEndpoints(RouteGroupBuilder group)
{
    group.MapGet("/modules", () => ModuleCatalog.All);

    AuthEndpoints.Map(group);
    ResourceEndpoints.Map(group);
    DataExchangeEndpoints.Map(group);
    PlatformEndpoints.Map(group);
    GovernanceEndpoints.Map(group);
    TicketEndpoints.Map(group);
    OperationalEndpoints.Map(group);
    FindingEndpoints.Map(group);

    group.MapPost("/ad/permissions/audit", (PermissionAuditRequest request) =>
        Results.Ok(AnalysisServices.AuditPermissions(request)));
    group.MapPost("/identity/policies/audit", (IdentityPolicyRequest request) =>
        Results.Ok(AnalysisServices.AuditIdentity(request)));
    group.MapPost("/kubernetes/health", (ClusterHealthRequest request) =>
        Results.Ok(AnalysisServices.AnalyzeCluster(request)));
    group.MapPost("/kubernetes/manifests", (ManifestRequest request) =>
        Results.Text(AnalysisServices.GenerateManifest(request), "application/yaml"));
    group.MapPost("/database/queries/analyze", (QueryAnalysisRequest request) =>
        Results.Ok(AnalysisServices.AnalyzeQuery(request)));
    group.MapPost("/database/schemas/compare", (SchemaComparisonRequest request) =>
        Results.Ok(AnalysisServices.CompareSchemas(request)));

    group.MapPost("/pki/certificates", async (CertificateIssueRequest request, SwissKnife.Core.Repositories.ResourceRepository resources, CancellationToken ct) =>
    {
        var certificate = AnalysisServices.IssueCertificate(request);
        await resources.CreateAsync(new(
            "pki",
            request.CommonName,
            "issued",
            System.Text.Json.JsonSerializer.Serialize(new { serialNumber = certificate.SerialNumber, notAfter = certificate.NotAfter.ToString("O") })), ct);
        return Results.Created($"/api/pki/certificates/{certificate.SerialNumber}", certificate);
    });
    group.MapPost("/pki/certificates/{serial}/revoke", async (string serial, SwissKnife.Core.Repositories.ResourceRepository resources, CancellationToken ct) =>
    {
        var revocation = await resources.CreateAsync(new(
            "pki",
            serial,
            "revoked",
            System.Text.Json.JsonSerializer.Serialize(new { revokedAt = DateTimeOffset.UtcNow.ToString("O") })), ct);
        return Results.Ok(revocation);
    });
    group.MapGet("/dotnet/profiler/snapshot", AnalysisServices.GetRuntimeMetrics);

    group.MapPost("/logs", async (LogInput input, LogStore store, CancellationToken ct) =>
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
    group.MapGet("/logs", async (
        string? level,
        string? source,
        string? text,
        string? tenant,
        int? take,
        LogStore store,
        CancellationToken ct) =>
        Results.Ok(await store.QueryAsync(level, source, text, tenant, take ?? 100, ct)));
}

MapAllEndpoints(app.MapGroup("/api"));
MapAllEndpoints(app.MapGroup("/api/v1"));

app.Run();

public partial class Program;
