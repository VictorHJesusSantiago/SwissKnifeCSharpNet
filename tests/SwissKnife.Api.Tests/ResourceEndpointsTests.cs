using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SwissKnife.Api.Tests;

public sealed class ResourceEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public ResourceEndpointsTests(ApiTestFactory factory) => _factory = factory;

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
        return client;
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous_and_returns_ok()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Any_mutating_call_without_api_key_is_rejected()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/resources", new { Module = "snippets", Name = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_then_get_resource_round_trips_payload_and_etag()
    {
        using var client = CreateClient();
        var create = await client.PostAsJsonAsync("/api/resources", new { Module = "snippets", Name = $"snippet-{Guid.NewGuid():N}", Status = "active", Data = new Dictionary<string, object> { ["language"] = "csharp" } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement;
        var id = created.GetProperty("id").GetGuid();

        var get = await client.GetAsync($"/api/resources/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.True(get.Headers.ETag is not null);
    }

    [Fact]
    public async Task List_with_empty_query_string_filters_returns_items_instead_of_treating_them_as_literal_filters()
    {
        using var client = CreateClient();
        var name = $"snippet-{Guid.NewGuid():N}";
        var create = await client.PostAsJsonAsync("/api/resources", new { Module = "snippets", Name = name, Status = "active" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var list = await client.GetAsync($"/api/resources?module=snippets&status=&text=&cursor=");
        var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(listJson.GetProperty("items").EnumerateArray(), x => x.GetProperty("name").GetString() == name);
    }

    [Fact]
    public async Task Creating_a_ticket_with_invalid_priority_is_rejected_by_schema_validation()
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/resources", new
        {
            Module = "tickets",
            Name = $"ticket-{Guid.NewGuid():N}",
            Data = new Dictionary<string, object> { ["type"] = "incident", ["priority"] = "not-a-real-priority" }
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Idempotency_key_replays_the_same_response_instead_of_creating_twice()
    {
        using var client = CreateClient();
        var name = $"idem-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new { Module = "snippets", Name = name, Status = "active" });

        using var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/resources") { Content = body };
        request1.Headers.Add("Idempotency-Key", "same-key-123");
        var response1 = await client.SendAsync(request1);

        using var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/resources")
        {
            Content = JsonContent.Create(new { Module = "snippets", Name = name, Status = "active" })
        };
        request2.Headers.Add("Idempotency-Key", "same-key-123");
        var response2 = await client.SendAsync(request2);

        var body1 = await response1.Content.ReadAsStringAsync();
        var body2 = await response2.Content.ReadAsStringAsync();
        Assert.True(response1.IsSuccessStatusCode, body1);
        Assert.Equal(body1, body2);

        var list = await client.GetAsync($"/api/resources?module=snippets&text={name}");
        var listBody = await list.Content.ReadAsStringAsync();
        var listJson = JsonDocument.Parse(listBody).RootElement;
        Assert.True(listJson.GetProperty("items").GetArrayLength() == 1, $"list body: {listBody}; created body: {body1}");
    }

    [Fact]
    public async Task Two_different_api_keys_from_different_tenants_do_not_see_each_others_resources()
    {
        using var bootstrapClient = CreateClient();

        var createTenant = await bootstrapClient.PostAsJsonAsync("/api/tenants", new { Slug = $"tenant-{Guid.NewGuid():N}", DisplayName = "Tenant de teste" });
        Assert.Equal(HttpStatusCode.Created, createTenant.StatusCode);
        var tenantId = JsonDocument.Parse(await createTenant.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var issueKey = await bootstrapClient.PostAsJsonAsync($"/api/tenants/{tenantId}/api-keys", new { Name = "chave-teste", Scopes = "*" });
        Assert.Equal(HttpStatusCode.Created, issueKey.StatusCode);
        var newApiKey = JsonDocument.Parse(await issueKey.Content.ReadAsStringAsync()).RootElement.GetProperty("plainTextKey").GetString()!;

        using var tenantClient = _factory.CreateClient();
        tenantClient.DefaultRequestHeaders.Add("X-Api-Key", newApiKey);

        var createUnderTenant = await tenantClient.PostAsJsonAsync("/api/resources", new { Module = "snippets", Name = $"tenant-only-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, createUnderTenant.StatusCode);
        var resourceId = JsonDocument.Parse(await createUnderTenant.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var getFromBootstrapTenant = await bootstrapClient.GetAsync($"/api/resources/{resourceId}");
        Assert.Equal(HttpStatusCode.NotFound, getFromBootstrapTenant.StatusCode);
    }
}
