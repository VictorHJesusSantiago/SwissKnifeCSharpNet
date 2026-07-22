namespace SwissKnife.Desktop;

/// <summary>UI-002: conecta o Desktop à API com uma API key de tenant antes de liberar qualquer tela de dados.</summary>
public sealed class LoginPage : ContentPage
{
    private readonly Entry _baseUrl = new() { Placeholder = "http://localhost:5000", Text = DesktopSession.BaseUrl };
    private readonly Entry _apiKey = new() { Placeholder = "Cole sua API key (sk_...)", IsPassword = true };
    private readonly Label _status = new() { TextColor = Colors.Gray };

    public LoginPage()
    {
        Title = "Conectar ao SwissKnife";

        var testButton = new Button { Text = "Testar conexão" };
        testButton.Clicked += async (_, _) => await TestAsync();

        var connectButton = new Button { Text = "Entrar", BackgroundColor = Colors.SeaGreen, TextColor = Colors.White };
        connectButton.Clicked += async (_, _) => await ConnectAsync();

        Content = new VerticalStackLayout
        {
            Padding = 32,
            Spacing = 14,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = "SwissKnife", FontSize = 28, FontAttributes = FontAttributes.Bold },
                new Label { Text = "Informe a URL da API e uma API key emitida para o seu tenant." },
                new Label { Text = "URL da API" },
                _baseUrl,
                new Label { Text = "API key" },
                _apiKey,
                new HorizontalStackLayout { Spacing = 10, Children = { testButton, connectButton } },
                _status
            }
        };
    }

    private async Task TestAsync()
    {
        _status.TextColor = Colors.Gray;
        _status.Text = "Testando...";
        using var client = new DesktopApiClient(_baseUrl.Text, _apiKey.Text ?? "");
        var ok = await client.TestConnectionAsync();
        _status.TextColor = ok ? Colors.SeaGreen : Colors.DarkRed;
        _status.Text = ok ? "Conexão OK." : "Falha ao conectar: verifique URL e API key.";
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(_apiKey.Text))
        {
            _status.TextColor = Colors.DarkRed;
            _status.Text = "Informe a API key.";
            return;
        }

        if (!Uri.TryCreate(_baseUrl.Text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            _status.TextColor = Colors.DarkRed;
            _status.Text = "Informe uma URL HTTP(S) absoluta válida.";
            return;
        }

        _status.TextColor = Colors.Gray;
        _status.Text = "Validando credencial...";
        using (var client = new DesktopApiClient(uri.ToString(), _apiKey.Text))
        {
            if (!await client.TestConnectionAsync())
            {
                _status.TextColor = Colors.DarkRed;
                _status.Text = "A API recusou a conexão ou a credencial.";
                return;
            }
        }

        DesktopSession.BaseUrl = uri.ToString();
        await DesktopSession.SetApiKeyAsync(_apiKey.Text);

        if (Application.Current is { } app)
            app.Windows[0].Page = new NavigationPage(new SnippetPage());
    }
}
