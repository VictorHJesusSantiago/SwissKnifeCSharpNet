using SwissKnife.Api;
using SwissKnife.Core.Auditing;
using SwissKnife.Core.Auth;
using SwissKnife.Core.Configuration;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Api.Endpoints;

public sealed record SetFeatureFlagRequest(string Key, Guid? TenantId, string? Environment, bool Enabled);
public sealed record SetDynamicConfigRequest(string Key, Guid? TenantId, string Value);
public sealed record GrantElevationRequest(Guid UserId, string Scope, string Justification, int DurationMinutes);

/// <summary>API-021/023/033/034: elevação temporária, auditoria, feature flags e configuração dinâmica.</summary>
public static class GovernanceEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var flags = api.MapGroup("/feature-flags").AddEndpointFilter<PlatformAdminFilter>();
        flags.MapGet("/", async (FeatureFlagService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));
        flags.MapPost("/", async (SetFeatureFlagRequest request, FeatureFlagService service, ITenantContext tenant, CancellationToken ct) =>
        {
            await service.SetAsync(request.Key, request.TenantId, request.Environment, request.Enabled, tenant.ActorName, ct);
            return Results.NoContent();
        });
        api.MapGet("/feature-flags/{key}/evaluate", async (string key, Guid? tenantId, string? environment, FeatureFlagService service, CancellationToken ct) =>
            Results.Ok(new { key, enabled = await service.IsEnabledAsync(key, tenantId, environment, ct) }));

        var config = api.MapGroup("/config").AddEndpointFilter<PlatformAdminFilter>();
        config.MapGet("/{key}", async (string key, Guid? tenantId, DynamicConfigService service, CancellationToken ct) =>
        {
            var value = await service.GetAsync(key, tenantId, ct);
            return value is null ? Results.NotFound() : Results.Ok(new { key, value });
        });
        config.MapPost("/", async (SetDynamicConfigRequest request, DynamicConfigService service, ITenantContext tenant, CancellationToken ct) =>
        {
            await service.SetAsync(request.Key, request.TenantId, request.Value, tenant.ActorName, ct);
            return Results.NoContent();
        });
        config.MapGet("/{key}/history", async (string key, Guid? tenantId, DynamicConfigService service, CancellationToken ct) =>
            Results.Ok(await service.HistoryAsync(key, tenantId, ct)));
        config.MapPost("/{key}/rollback/{version:int}", async (string key, int version, Guid? tenantId, DynamicConfigService service, ITenantContext tenant, CancellationToken ct) =>
            await service.RollbackAsync(key, tenantId, version, tenant.ActorName, ct) ? Results.NoContent() : Results.NotFound());

        var audit = api.MapGroup("/audit-log").AddEndpointFilter<PlatformAdminFilter>();
        audit.MapGet("/", async (Guid? tenantId, string? action, int? take, AuditLogger logger, CancellationToken ct) =>
            Results.Ok(await logger.QueryAsync(tenantId, action, take ?? 100, ct)));

        var elevation = api.MapGroup("/elevations").AddEndpointFilter<PlatformAdminFilter>();
        elevation.MapPost("/", async (GrantElevationRequest request, ElevationService service, ITenantContext tenant, CancellationToken ct) =>
        {
            var granted = await service.GrantAsync(request.UserId, request.Scope, request.Justification, TimeSpan.FromMinutes(Math.Clamp(request.DurationMinutes, 1, 480)), tenant.ActorName, ct);
            return Results.Created($"/api/elevations/{granted.Id}", granted);
        });
        elevation.MapPost("/{id:guid}/revoke", async (Guid id, ElevationService service, CancellationToken ct) =>
            await service.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }
}
