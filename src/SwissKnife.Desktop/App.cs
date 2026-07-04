namespace SwissKnife.Desktop;

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
        => new(new SnippetPage()) { Title = "SwissKnife Snippets" };
}
