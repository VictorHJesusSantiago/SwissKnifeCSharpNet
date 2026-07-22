using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SwissKnife.Api.Tests;

public sealed class FindingEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    public FindingEndpointsTests(ApiTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Finding_deduplica_aceita_risco_e_resolve()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
        var input = new
        {
            module = "multi-cloud", code = "CLD_PUBLIC_EXPOSURE", severity = "High",
            title = "Recurso exposto", deduplicationKey = "azure:vm-42",
            evidence = new { address = "203.0.113.10" }
        };
        var first = await client.PostAsJsonAsync("/api/findings", input);
        var second = await client.PostAsJsonAsync("/api/findings", input);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(2, body.GetProperty("occurrenceCount").GetInt32());

        var decision = await client.PostAsJsonAsync($"/api/findings/{id}/decision", new
        {
            status = "RiskAccepted", reason = "Exposição temporária aprovada pelo responsável",
            expiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        var resolve = await client.PostAsJsonAsync($"/api/findings/{id}/resolve", new { });
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
    }
}
