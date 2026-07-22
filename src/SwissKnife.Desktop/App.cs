namespace SwissKnife.Desktop;

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Página inicial síncrona (LoginPage) evita Window.Page nulo enquanto a checagem de
        // sessão assíncrona não termina; se já houver API key salva, trocamos para as
        // telas de dados logo em seguida.
        var window = new Window(new LoginPage()) { Title = "SwissKnife" };
        _ = InitializeAsync(window);
        return window;
    }

    private static async Task InitializeAsync(Window window)
    {
        var configured = await DesktopSession.IsConfiguredAsync();
        if (configured) window.Page = new NavigationPage(new SnippetPage());
    }
}
