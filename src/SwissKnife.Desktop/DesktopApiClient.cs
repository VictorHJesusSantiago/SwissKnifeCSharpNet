using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SwissKnife.Core;

namespace SwissKnife.Desktop;

/// <summary>Cliente HTTP mínimo do Desktop para a API SwissKnife (mesmo princípio do ApiClient da CLI).</summary>
public sealed class DesktopApiClient(string baseUrl, string apiKey) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };

    private void Prepare(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        Prepare(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object body, string? ifMatch = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Options), Encoding.UTF8, "application/json")
        };
        Prepare(request);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        Prepare(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var health = await _http.GetAsync("health/live", cancellationToken);
            if (!health.IsSuccessStatusCode) return false;
            await GetAsync("api/modules", cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Falha na API ({(int)response.StatusCode}): {body}");
    }

    public void Dispose() => _http.Dispose();
}
