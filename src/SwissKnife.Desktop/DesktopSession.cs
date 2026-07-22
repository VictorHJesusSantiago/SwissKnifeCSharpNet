namespace SwissKnife.Desktop;

/// <summary>
/// UI-002: sessão do Desktop com a API (base URL + API key), persistida localmente.
/// A chave fica em SecureStorage (criptografada pelo SO); a URL fica em Preferences (não é
/// segredo). Sem isso, o Desktop ficaria preso ao acesso direto ao arquivo local, sem
/// isolamento de tenant real (mesmo motivo pelo qual a CLI migrou para HTTP na Fundação).
/// </summary>
public static class DesktopSession
{
    private const string BaseUrlKey = "swissknife.base_url";
    private const string ApiKeySecureKey = "swissknife.api_key";

    public static string BaseUrl
    {
        get => Preferences.Default.Get(BaseUrlKey, "http://localhost:5000");
        set => Preferences.Default.Set(BaseUrlKey, value);
    }

    public static async Task<string?> GetApiKeyAsync() => await SecureStorage.Default.GetAsync(ApiKeySecureKey);

    public static async Task SetApiKeyAsync(string apiKey) => await SecureStorage.Default.SetAsync(ApiKeySecureKey, apiKey);

    public static void ClearApiKey() => SecureStorage.Default.Remove(ApiKeySecureKey);

    public static async Task<bool> IsConfiguredAsync() => !string.IsNullOrWhiteSpace(await GetApiKeyAsync());
}
