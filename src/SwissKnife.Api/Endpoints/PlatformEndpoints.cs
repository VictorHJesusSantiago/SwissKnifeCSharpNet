using Microsoft.EntityFrameworkCore;
using SwissKnife.Api;
using SwissKnife.Core.Auditing;
using SwissKnife.Core.Backup;
using SwissKnife.Core.Entities;
using SwissKnife.Core.ImportExport;
using SwissKnife.Core.Jobs;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Security;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Api.Endpoints;

public sealed record CreateTenantRequest(string Slug, string DisplayName);
public sealed record IssueApiKeyRequest(string Name, string Scopes = "*", DateTimeOffset? ExpiresAt = null);
public sealed record SetLimitRequest(string ResourceType, int? MaxCount, long? MaxStorageBytes, int? MaxJobsConcurrent);
public sealed record SetSettingRequest(string Key, string Value);
public sealed record CreateOrgUnitRequest(string Name, OrgUnitKind Kind, Guid? ParentId);
public sealed record EnqueueJobRequest(string Kind, string? PayloadJson);
public sealed record CreateScheduleRequest(string Kind, string CronExpression, string TimeZoneId = "UTC", string? PayloadJson = null);
public sealed record StoreSecretRequest(string Name, string Value);

public static class PlatformEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        MapTenantAdmin(api);
        MapJobs(api);
        MapImportExport(api);
        MapSecrets(api);
        MapBackup(api);
    }

    private static void MapTenantAdmin(RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/tenants").AddEndpointFilter<PlatformAdminFilter>();

        admin.MapGet("/", async (TenantService tenants, CancellationToken ct) => Results.Ok(await tenants.ListAsync(ct)));
        admin.MapPost("/", async (CreateTenantRequest request, TenantService tenants, CancellationToken ct) =>
            Results.Created("/api/tenants", await tenants.CreateAsync(request.Slug, request.DisplayName, ct)));
        admin.MapPost("/{tenantId:guid}/suspend", async (Guid tenantId, TenantService tenants, AuditLogger audit, ITenantContext actor, CancellationToken ct) =>
        {
            var ok = await tenants.SuspendAsync(tenantId, ct);
            if (ok) await audit.LogAsync(tenantId, actor.ActorName, "tenant.suspended", "Tenant", tenantId.ToString(), cancellationToken: ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });
        admin.MapPost("/{tenantId:guid}/reactivate", async (Guid tenantId, TenantService tenants, AuditLogger audit, ITenantContext actor, CancellationToken ct) =>
        {
            var ok = await tenants.ReactivateAsync(tenantId, ct);
            if (ok) await audit.LogAsync(tenantId, actor.ActorName, "tenant.reactivated", "Tenant", tenantId.ToString(), cancellationToken: ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });
        admin.MapDelete("/{tenantId:guid}", async (Guid tenantId, TenantService tenants, AuditLogger audit, ITenantContext actor, CancellationToken ct) =>
        {
            var ok = await tenants.DeleteAsync(tenantId, ct);
            if (ok) await audit.LogAsync(tenantId, actor.ActorName, "tenant.deleted", "Tenant", tenantId.ToString(), cancellationToken: ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        admin.MapPost("/{tenantId:guid}/limits", async (Guid tenantId, SetLimitRequest request, TenantService tenants, CancellationToken ct) =>
        {
            await tenants.SetLimitAsync(tenantId, request.ResourceType, request.MaxCount, request.MaxStorageBytes, request.MaxJobsConcurrent, ct);
            return Results.NoContent();
        });
        admin.MapPost("/{tenantId:guid}/settings", async (Guid tenantId, SetSettingRequest request, TenantService tenants, CancellationToken ct) =>
        {
            await tenants.SetSettingAsync(tenantId, request.Key, request.Value, ct);
            return Results.NoContent();
        });

        admin.MapPost("/{tenantId:guid}/org-units", async (Guid tenantId, CreateOrgUnitRequest request, TenantService tenants, CancellationToken ct) =>
            Results.Created($"/api/tenants/{tenantId}/org-units", await tenants.CreateOrgUnitAsync(tenantId, request.Name, request.Kind, request.ParentId, ct)));
        admin.MapGet("/{tenantId:guid}/org-units", async (Guid tenantId, TenantService tenants, CancellationToken ct) =>
            Results.Ok(await tenants.ListOrgUnitsAsync(tenantId, ct)));

        admin.MapPost("/{tenantId:guid}/api-keys", async (Guid tenantId, IssueApiKeyRequest request, ApiKeyService keys, AuditLogger audit, ITenantContext actor, CancellationToken ct) =>
        {
            var issued = await keys.IssueAsync(tenantId, request.Name, request.Scopes, request.ExpiresAt, ct);
            await audit.LogAsync(tenantId, actor.ActorName, "apikey.issued", "ApiKey", issued.Id.ToString(), new { request.Name, request.Scopes }, cancellationToken: ct);
            return Results.Created($"/api/tenants/{tenantId}/api-keys", new { issued.Id, issued.PlainTextKey, issued.Prefix, warning = "Guarde esta chave agora: ela não será exibida novamente." });
        });
        admin.MapDelete("/api-keys/{apiKeyId:guid}", async (Guid apiKeyId, ApiKeyService keys, AuditLogger audit, ITenantContext actor, CancellationToken ct) =>
        {
            var revoked = await keys.RevokeAsync(apiKeyId, ct);
            if (revoked) await audit.LogAsync(null, actor.ActorName, "apikey.revoked", "ApiKey", apiKeyId.ToString(), cancellationToken: ct);
            return revoked ? Results.NoContent() : Results.NotFound();
        });
    }

    private static void MapJobs(RouteGroupBuilder api)
    {
        var jobs = api.MapGroup("/jobs");
        jobs.MapPost("/", async (EnqueueJobRequest request, SwissKnifeDbContext db, JobQueue queue, ITenantContext tenant, CancellationToken ct) =>
        {
            var entity = new JobEntity { TenantId = tenant.TenantId, Kind = request.Kind, PayloadJson = request.PayloadJson };
            db.Jobs.Add(entity);
            await db.SaveChangesAsync(ct);
            await queue.EnqueueAsync(new JobEnvelope(entity.Id, entity.TenantId, entity.Kind, entity.PayloadJson), ct);
            return Results.Created($"/api/jobs/{entity.Id}", entity);
        });
        jobs.MapGet("/{id:guid}", async (Guid id, SwissKnifeDbContext db, ITenantContext tenant, CancellationToken ct) =>
            await db.Jobs.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenant.TenantId, ct) is { } job ? Results.Ok(job) : Results.NotFound());
        jobs.MapGet("/", async (SwissKnifeDbContext db, ITenantContext tenant, CancellationToken ct) =>
            Results.Ok(await db.Jobs.AsNoTracking().Where(x => x.TenantId == tenant.TenantId).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct)));
        jobs.MapPost("/{id:guid}/cancel", (Guid id, JobDispatcherHostedService dispatcher) =>
            dispatcher.TryCancel(id) ? Results.NoContent() : Results.NotFound());

        var schedules = api.MapGroup("/scheduled-jobs");
        schedules.MapPost("/", async (CreateScheduleRequest request, SwissKnifeDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var entity = new ScheduledJobEntity { TenantId = tenant.TenantId, Kind = request.Kind, CronExpression = request.CronExpression, TimeZoneId = request.TimeZoneId, PayloadJson = request.PayloadJson };
            db.ScheduledJobs.Add(entity);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/scheduled-jobs/{entity.Id}", entity);
        });
        schedules.MapGet("/", async (SwissKnifeDbContext db, ITenantContext tenant, CancellationToken ct) =>
            Results.Ok(await db.ScheduledJobs.AsNoTracking().Where(x => x.TenantId == tenant.TenantId || x.TenantId == null).ToListAsync(ct)));
    }

    private static void MapImportExport(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/import-export");
        group.MapPost("/{module}/import", async (string module, ImportFormat format, HttpRequest http, ImportExportService service, CancellationToken ct) =>
        {
            if (!http.HasFormContentType) return Results.BadRequest(new { error = "Envie multipart/form-data com o campo 'file'." });
            var form = await http.ReadFormAsync(ct);
            var file = form.Files["file"] ?? throw new ArgumentException("Campo 'file' ausente.");
            await using var stream = file.OpenReadStream();
            return Results.Ok(await service.ImportAsync(module, format, stream, ct));
        });
        group.MapGet("/{module}/export", async (string module, ImportFormat format, ImportExportService service, CancellationToken ct) =>
            Results.Text(await service.ExportAsync(module, format, ct), format == ImportFormat.Json ? "application/json" : "text/plain"));
    }

    private static void MapSecrets(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/secrets");
        group.MapPost("/", async (StoreSecretRequest request, ISecretVault vault, ITenantContext tenant, CancellationToken ct) =>
            Results.Created("/api/secrets", new { Id = await vault.StoreAsync(tenant.TenantId, request.Name, request.Value, ct) }));
        group.MapGet("/", async (ISecretVault vault, ITenantContext tenant, CancellationToken ct) =>
            Results.Ok(await vault.ListAsync(tenant.TenantId, ct)));
        group.MapPost("/{id:guid}/reveal", async (Guid id, ISecretVault vault, ITenantContext tenant, AuditLogger audit, CancellationToken ct) =>
        {
            if (!tenant.HasScope("data:reveal"))
                return Results.Json(new { error = "Requer escopo data:reveal." }, statusCode: StatusCodes.Status403Forbidden);
            var value = await vault.RevealAsync(tenant.TenantId, id, ct);
            await audit.LogAsync(tenant.TenantId, tenant.ActorName, "secret.revealed", "Secret", id.ToString(), success: value is not null, cancellationToken: ct);
            return value is null ? Results.NotFound() : Results.Ok(new { value });
        });
    }

    private static void MapBackup(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/backup").AddEndpointFilter<PlatformAdminFilter>();
        group.MapPost("/", async (SqliteBackupService backup, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var path = Path.Combine(env.ContentRootPath, "data", "backups", $"backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.zip");
            var created = await backup.CreateBackupAsync(path, ct);
            return Results.Ok(new { path = created });
        });
    }

}
