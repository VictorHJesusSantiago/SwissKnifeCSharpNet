using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwissKnife.Desktop;

public sealed record SnippetItem(Guid Id, string Name, string Language, string Code, string ETag);

/// <summary>
/// Editor de snippets ligado à API (não mais a um arquivo local isolado) — SNP-001/002/014/015.
/// </summary>
public sealed partial class SnippetPage : ContentPage
{
    private readonly ObservableCollection<SnippetItem> _snippets = [];
    private readonly Entry _search = new() { Placeholder = "Pesquisar por nome, linguagem ou conteúdo..." };
    private readonly Entry _name = new() { Placeholder = "Nome do snippet" };
    private readonly Entry _language = new() { Placeholder = "Linguagem (csharp, sql...)" };
    private readonly Editor _code = new()
    {
        Placeholder = "Cole seu código aqui",
        AutoSize = EditorAutoSizeOption.TextChanges,
        MinimumHeightRequest = 180,
        FontFamily = "Consolas"
    };
    private readonly Label _status = new() { TextColor = Colors.Gray };
    private readonly CollectionView _list;
    private CancellationTokenSource? _searchDebounce;
    private CancellationTokenSource? _clipboardClear;

    // SNP-001: acompanha o item em edição para fazer UPDATE em vez de recriar um duplicata.
    private Guid? _editingId;
    private string? _editingETag;

    public SnippetPage()
    {
        Title = "Snippet manager";

        _search.TextChanged += async (_, _) =>
        {
            _searchDebounce?.Cancel();
            _searchDebounce?.Dispose();
            _searchDebounce = new CancellationTokenSource();
            try
            {
                await Task.Delay(300, _searchDebounce.Token);
                await LoadAsync();
            }
            catch (OperationCanceledException) { }
        };

        _list = new CollectionView
        {
            ItemsSource = _snippets,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var name = new Label { FontAttributes = FontAttributes.Bold, FontSize = 16 };
                name.SetBinding(Label.TextProperty, nameof(SnippetItem.Name));
                var language = new Label { FontSize = 12, TextColor = Colors.Gray };
                language.SetBinding(Label.TextProperty, nameof(SnippetItem.Language));
                return new VerticalStackLayout { Padding = 8, Children = { name, language } };
            })
        };
        _list.SelectionChanged += (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is not SnippetItem selected)
            {
                _editingId = null;
                _editingETag = null;
                return;
            }
            _editingId = selected.Id;
            _editingETag = selected.ETag;
            _name.Text = selected.Name;
            _language.Text = selected.Language;
            _code.Text = selected.Code;
        };

        var save = new Button { Text = "Salvar snippet" };
        save.Clicked += async (_, _) => await SaveAsync();
        var clear = new Button { Text = "Novo" };
        clear.Clicked += (_, _) =>
        {
            _list.SelectedItem = null;
            _editingId = null;
            _editingETag = null;
            _name.Text = _language.Text = _code.Text = string.Empty;
        };
        var copy = new Button { Text = "Copiar código" };
        copy.Clicked += async (_, _) =>
        {
            var copied = _code.Text;
            if (string.IsNullOrEmpty(copied)) return;
            await Clipboard.Default.SetTextAsync(copied);
            _status.Text = "Código copiado; a área de transferência será limpa em 30 segundos.";
            _clipboardClear?.Cancel();
            _clipboardClear?.Dispose();
            _clipboardClear = new CancellationTokenSource();
            _ = ClearClipboardAsync(copied, _clipboardClear.Token);
        };
        var delete = new Button { Text = "Excluir", TextColor = Colors.DarkRed };
        delete.Clicked += async (_, _) =>
        {
            if (_list.SelectedItem is not SnippetItem selected) return;
            var confirmed = await DisplayAlertAsync("Excluir snippet", $"Mover \"{selected.Name}\" para a lixeira?", "Excluir", "Cancelar");
            if (!confirmed) return;
            using var client = await CreateClientAsync();
            await client.DeleteAsync($"api/resources/{selected.Id}");
            _editingId = null;
            _editingETag = null;
            await LoadAsync();
        };
        var logout = new Button { Text = "Sair", TextColor = Colors.Gray };
        logout.Clicked += (_, _) =>
        {
            DesktopSession.ClearApiKey();
            if (Application.Current is { } app) app.Windows[0].Page = new LoginPage();
        };

        var editor = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                new Label { Text = "Snippet", FontSize = 24, FontAttributes = FontAttributes.Bold },
                _name,
                _language,
                _code,
                new HorizontalStackLayout { Spacing = 8, Children = { save, clear, copy, delete, logout } },
                _status
            }
        };
        Grid.SetColumn(editor, 1);
        Grid.SetRow(editor, 1);

        var listBorder = new Border
        {
            Stroke = Colors.LightGray,
            Content = _list,
            Margin = new Thickness(0, 8, 12, 0)
        };
        Grid.SetRow(listBorder, 1);

        Grid.SetColumnSpan(_search, 2);

        Content = new Grid
        {
            Padding = 20,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(new GridLength(1, GridUnitType.Star)) },
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(2, GridUnitType.Star))
            },
            Children = { _search, listBorder, editor }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private static async Task<DesktopApiClient> CreateClientAsync()
    {
        var apiKey = await DesktopSession.GetApiKeyAsync() ?? throw new InvalidOperationException("Sessão não configurada.");
        return new DesktopApiClient(DesktopSession.BaseUrl, apiKey);
    }

    private async Task ClearClipboardAsync(string copied, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            if (await Clipboard.Default.GetTextAsync() == copied)
            {
                await Clipboard.Default.SetTextAsync(string.Empty);
                _status.Text = "Área de transferência limpa.";
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadAsync()
    {
        try
        {
            using var client = await CreateClientAsync();
            var query = string.IsNullOrWhiteSpace(_search.Text) ? "" : $"&text={Uri.EscapeDataString(_search.Text)}";
            var document = await client.GetAsync($"api/resources?module=snippets{query}");
            _snippets.Clear();
            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                var data = item.GetProperty("data");
                _snippets.Add(new SnippetItem(
                    item.GetProperty("id").GetGuid(),
                    item.GetProperty("name").GetString() ?? "",
                    data.TryGetProperty("language", out var lang) ? lang.GetString() ?? "text" : "text",
                    data.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "",
                    item.GetProperty("eTag").GetString() ?? ""));
            }
            _status.TextColor = Colors.Gray;
            _status.Text = $"{_snippets.Count} snippet(s)";
        }
        catch (Exception exception)
        {
            _status.TextColor = Colors.DarkRed;
            _status.Text = $"Falha ao carregar: {exception.Message}";
        }
    }

    // SNP-014: heurística simples para detectar segredos óbvios antes de salvar/compartilhar.
    private static readonly Regex[] SecretPatterns =
    [
        SecretAwsKey(), SecretGenericApiKey(), SecretPassword(), SecretPrivateKeyBlock()
    ];

    [GeneratedRegex(@"AKIA[0-9A-Z]{16}")]
    private static partial Regex SecretAwsKey();
    [GeneratedRegex(@"(?i)(api[_-]?key|token|secret)\s*[:=]\s*['""]?[A-Za-z0-9\-_.]{16,}")]
    private static partial Regex SecretGenericApiKey();
    [GeneratedRegex(@"(?i)password\s*[:=]\s*['""]?\S{6,}")]
    private static partial Regex SecretPassword();
    [GeneratedRegex(@"-----BEGIN (RSA |EC )?PRIVATE KEY-----")]
    private static partial Regex SecretPrivateKeyBlock();

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _status.TextColor = Colors.DarkRed;
            _status.Text = "Informe um nome.";
            return;
        }

        var code = _code.Text ?? "";
        if (SecretPatterns.Any(pattern => pattern.IsMatch(code)))
        {
            var proceed = await DisplayAlertAsync(
                "Possível segredo detectado",
                "Este snippet parece conter uma credencial, chave ou senha. Tem certeza que quer salvar assim mesmo?",
                "Salvar mesmo assim", "Cancelar");
            if (!proceed) return;
        }

        try
        {
            using var client = await CreateClientAsync();
            var body = new
            {
                Module = "snippets",
                Name = _name.Text,
                Status = "active",
                Data = new Dictionary<string, object?> { ["language"] = _language.Text ?? "text", ["code"] = code }
            };

            if (_editingId is { } id)
                await client.SendJsonAsync(HttpMethod.Put, $"api/resources/{id}", body, _editingETag);
            else
                await client.SendJsonAsync(HttpMethod.Post, "api/resources", body);

            _editingId = null;
            _editingETag = null;
            _name.Text = _language.Text = _code.Text = string.Empty;
            await LoadAsync();
        }
        catch (Exception exception)
        {
            _status.TextColor = Colors.DarkRed;
            _status.Text = $"Falha ao salvar: {exception.Message}";
        }
    }
}
