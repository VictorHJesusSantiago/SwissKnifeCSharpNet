using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SwissKnife.Api.Tests;

public sealed class OperationalEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public OperationalEndpointsTests(ApiTestFactory factory) => _factory = factory;

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
        return client;
    }

    [Theory]
    [InlineData("/api/operations/vpn/audit")]
    [InlineData("/api/v1/operations/vpn/audit")]
    public async Task Vpn_audit_esta_disponivel_nas_duas_versoes(string path)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync(path, new
        {
            name = "admin",
            routes = new[] { "0.0.0.0/0" },
            dnsServers = new[] { "1.1.1.1" },
            fullTunnel = true,
            killSwitch = false,
            expiresAt = DateTimeOffset.UtcNow.AddDays(10),
            lastUsedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("compliant").GetBoolean());
        Assert.Contains(body.GetProperty("findings").EnumerateArray(),
            x => x.GetProperty("code").GetString() == "VPN_KILL_SWITCH_DISABLED");
    }

    [Fact]
    public async Task Webhook_sign_e_verify_fazem_roundtrip()
    {
        using var client = CreateClient();
        const string secret = "0123456789abcdef0123456789abcdef";
        const string payload = "{\"event\":\"ticket.created\"}";
        var sign = await client.PostAsJsonAsync("/api/operations/webhooks/sign", new { payload, secret, eventId = "evt-1" });
        Assert.Equal(HttpStatusCode.OK, sign.StatusCode);
        var signature = JsonDocument.Parse(await sign.Content.ReadAsStringAsync()).RootElement.Clone();

        var verify = await client.PostAsJsonAsync("/api/operations/webhooks/verify", new
        {
            payload,
            secret,
            signature,
            toleranceSeconds = 300
        });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.True(JsonDocument.Parse(await verify.Content.ReadAsStringAsync()).RootElement.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Lote_de_logs_redige_segredo_e_rejeita_severity_invalida()
    {
        using var client = CreateClient();
        var now = DateTimeOffset.UtcNow;
        var response = await client.PostAsJsonAsync("/api/operations/logs/validate-batch", new
        {
            entries = new object[]
            {
                new { timestamp = now, severity = "error", service = "api", environment = "prod", message = "password=abc", traceId = "t", spanId = "s" },
                new { timestamp = now, severity = "invalid", service = "api", environment = "prod", message = "x" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Single(body.GetProperty("accepted").EnumerateArray());
        Assert.Single(body.GetProperty("rejected").EnumerateArray());
        Assert.Equal("password=***REDACTED***", body.GetProperty("accepted")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Execucao_operacional_persistida_mantem_resultado_e_historico()
    {
        using var client = CreateClient();
        var name = $"dr-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var response = await client.PostAsJsonAsync("/api/operations/executions", new
        {
            operation = "dr-assess",
            name,
            input = new
            {
                service = "billing",
                targetRtoMinutes = 60,
                targetRpoMinutes = 15,
                observedRtoMinutes = 30,
                observedRpoMinutes = 10,
                lastSuccessfulBackup = now.AddHours(-1),
                lastRestoreTest = now.AddDays(-10),
                runbookHasOwner = true,
                dependenciesCovered = true,
                smokeTestsPassed = true
            }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var id = body.GetProperty("resource").GetProperty("id").GetGuid();
        Assert.Equal("ready", body.GetProperty("result").GetProperty("status").GetString());

        var resource = await client.GetAsync($"/api/resources/{id}");
        Assert.Equal(HttpStatusCode.OK, resource.StatusCode);
        var history = await client.GetAsync($"/api/resources/{id}/history");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.NotEmpty(JsonDocument.Parse(await history.Content.ReadAsStringAsync()).RootElement.EnumerateArray());
    }
}
