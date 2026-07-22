using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SwissKnife.Core;

namespace SwissKnife.Cli;

/// <summary>
/// FND-031: a CLI passa a falar HTTP com a API em vez de acessar o arquivo/DB diretamente,
/// porque o isolamento multi-tenant real só existe quando a identidade é resolvida pela
/// API autenticada — acesso direto bypassa tenant, escopos, outbox e idempotência.
/// </summary>
public sealed class ApiClient(string baseUrl, string apiKey) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };

    private void Prepare(HttpRequestMessage request, string? idempotencyKey = null, string? ifMatch = null)
    {
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
    }

    public async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        Prepare(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string path,
        object body,
        string? idempotencyKey = null,
        string? ifMatch = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Options), Encoding.UTF8, "application/json")
        };
        Prepare(request, idempotencyKey, ifMatch);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);
    }

    public async Task<JsonDocument> PostFileAsync(string path, Stream fileContent, string fileName, CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        using var streamContent = new StreamContent(fileContent);
        content.Add(streamContent, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        Prepare(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);
    }

    public async Task<string> GetTextAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        Prepare(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        Prepare(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Falha na API ({(int)response.StatusCode}): {body}");
    }

    public void Dispose() => _http.Dispose();
}
