using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SwissKnife.Api;
using SwissKnife.Core;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = builder.Configuration["SwissKnife:DataDirectory"] ?? "data";
var absoluteDataDirectory = Path.GetFullPath(dataDirectory, builder.Environment.ContentRootPath);
var requestsPerMinute = builder.Configuration.GetValue("SwissKnife:RequestsPerMinute", 120);

builder.Services.AddSingleton<IResourceStore>(new JsonResourceStore(Path.Combine(absoluteDataDirectory, "resources.json")));
builder.Services.AddSingleton(new LogStore(Path.Combine(absoluteDataDirectory, "logs.ndjson")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Request.Headers["X-Tenant-Id"].FirstOrDefault() ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = requestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<ApiKeyMiddleware>();

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

api.MapGet("/resources", async (string? module, string? tenant, IResourceStore store, CancellationToken ct) =>
    Results.Ok(await store.ListAsync(module, tenant, ct)));
api.MapGet("/resources/{id:guid}", async (Guid id, IResourceStore store, CancellationToken ct) =>
    await store.GetAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound());
api.MapPost("/resources", async (CreateResource request, IResourceStore store, CancellationToken ct) =>
{
    var item = await store.CreateAsync(request, ct);
    return Results.Created($"/api/resources/{item.Id}", item);
});
api.MapPut("/resources/{id:guid}", async (Guid id, CreateResource request, IResourceStore store, CancellationToken ct) =>
    await store.UpdateAsync(id, request, ct) is { } item ? Results.Ok(item) : Results.NotFound());
api.MapDelete("/resources/{id:guid}", async (Guid id, IResourceStore store, CancellationToken ct) =>
    await store.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

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
api.MapPost("/pki/certificates", async (CertificateIssueRequest request, IResourceStore store, CancellationToken ct) =>
{
    var certificate = AnalysisServices.IssueCertificate(request);
    await store.CreateAsync(new(
        "pki",
        request.CommonName,
        Status: "issued",
        Data: new()
        {
            ["serialNumber"] = certificate.SerialNumber,
            ["notAfter"] = certificate.NotAfter.ToString("O")
        }), ct);
    return Results.Created($"/api/pki/certificates/{certificate.SerialNumber}", certificate);
});
api.MapPost("/pki/certificates/{serial}/revoke", async (string serial, IResourceStore store, CancellationToken ct) =>
{
    var revocation = await store.CreateAsync(new(
        "pki",
        serial,
        Status: "revoked",
        Data: new() { ["revokedAt"] = DateTimeOffset.UtcNow.ToString("O") }), ct);
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
