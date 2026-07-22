using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SwissKnife.Api.Tests;

public sealed class GovernanceEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public GovernanceEndpointsTests(ApiTestFactory factory) => _factory = factory;

    private HttpClient BootstrapClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
        return client;
    }

    [Fact]
    public async Task Feature_flag_can_be_set_and_evaluated()
    {
        using var client = BootstrapClient();
        var key = $"flag-{Guid.NewGuid():N}";

        var set = await client.PostAsJsonAsync("/api/feature-flags", new { Key = key, TenantId = (Guid?)null, Environment = (string?)null, Enabled = true });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var evaluate = await client.GetAsync($"/api/feature-flags/{key}/evaluate");
        var json = JsonDocument.Parse(await evaluate.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Dynamic_config_keeps_history_and_supports_rollback()
    {
        using var client = BootstrapClient();
        var key = $"config-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync("/api/config", new { Key = key, TenantId = (Guid?)null, Value = "v1" });
        await client.PostAsJsonAsync("/api/config", new { Key = key, TenantId = (Guid?)null, Value = "v2" });

        var current = await client.GetAsync($"/api/config/{key}");
        var currentJson = JsonDocument.Parse(await current.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("v2", currentJson.GetProperty("value").GetString());

        var rollback = await client.PostAsync($"/api/config/{key}/rollback/1", null);
        Assert.Equal(HttpStatusCode.NoContent, rollback.StatusCode);

        var afterRollback = await client.GetAsync($"/api/config/{key}");
        var afterJson = JsonDocument.Parse(await afterRollback.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("v1", afterJson.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Sensitive_admin_actions_are_recorded_in_the_audit_log()
    {
        using var client = BootstrapClient();
        var createTenant = await client.PostAsJsonAsync("/api/tenants", new { Slug = $"audit-{Guid.NewGuid():N}", DisplayName = "Audit Co" });
        var tenantId = JsonDocument.Parse(await createTenant.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        await client.PostAsJsonAsync($"/api/tenants/{tenantId}/api-keys", new { Name = "chave-audit" });

        var auditLog = await client.GetAsync("/api/audit-log?action=apikey.issued");
        var json = JsonDocument.Parse(await auditLog.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.EnumerateArray().Any());
    }

    [Fact]
    public async Task Api_is_reachable_under_both_unversioned_and_v1_prefixes()
    {
        using var client = BootstrapClient();
        var unversioned = await client.GetAsync("/api/modules");
        var versioned = await client.GetAsync("/api/v1/modules");
        Assert.Equal(HttpStatusCode.OK, unversioned.StatusCode);
        Assert.Equal(HttpStatusCode.OK, versioned.StatusCode);
    }

    [Fact]
    public async Task Health_liveness_and_readiness_are_split_and_both_healthy()
    {
        using var client = _factory.CreateClient();
        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Errors_are_returned_as_rfc9457_problem_details_with_correlation_id()
    {
        using var client = BootstrapClient();
        // GET /resources/{id} devolve 404 direto (sem exceção); para exercitar o
        // ErrorHandlingMiddleware de verdade usamos uma rota que lança KeyNotFoundException.
        var response = await client.PostAsync($"/api/resources/{Guid.NewGuid()}/versions/1/restore", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.Contains("X-Correlation-Id"));

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(404, json.GetProperty("status").GetInt32());
        Assert.True(json.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task Temporary_elevation_requires_justification_and_expires()
    {
        using var client = BootstrapClient();
        var userId = Guid.NewGuid();

        var missingJustification = await client.PostAsJsonAsync("/api/elevations", new { UserId = userId, Scope = "data:reveal", Justification = "", DurationMinutes = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, missingJustification.StatusCode);

        var granted = await client.PostAsJsonAsync("/api/elevations", new { UserId = userId, Scope = "data:reveal", Justification = "Investigação de incidente INC-123", DurationMinutes = 30 });
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
    }
}
