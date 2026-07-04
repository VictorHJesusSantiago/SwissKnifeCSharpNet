using System.Collections.ObjectModel;
using SwissKnife.Core;

namespace SwissKnife.Desktop;

public sealed class SnippetPage : ContentPage
{
    private readonly JsonResourceStore _store =
        new(Path.Combine(FileSystem.AppDataDirectory, "snippets.json"));
    private readonly ObservableCollection<ResourceRecord> _snippets = [];
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

    public SnippetPage()
    {
        Title = "Snippet manager";
        var list = new CollectionView
        {
            ItemsSource = _snippets,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var name = new Label { FontAttributes = FontAttributes.Bold, FontSize = 16 };
                name.SetBinding(Label.TextProperty, nameof(ResourceRecord.Name));
                var language = new Label { FontSize = 12, TextColor = Colors.Gray };
                language.SetBinding(Label.TextProperty, "Data[language]");
                return new VerticalStackLayout { Padding = 8, Children = { name, language } };
            })
        };
        list.SelectionChanged += (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is not ResourceRecord selected) return;
            _name.Text = selected.Name;
            _language.Text = selected.Data.GetValueOrDefault("language");
            _code.Text = selected.Data.GetValueOrDefault("code");
        };

        var save = new Button { Text = "Salvar snippet" };
        save.Clicked += async (_, _) => await SaveAsync();
        var clear = new Button { Text = "Novo" };
        clear.Clicked += (_, _) =>
        {
            list.SelectedItem = null;
            _name.Text = _language.Text = _code.Text = string.Empty;
        };
        var delete = new Button { Text = "Excluir", TextColor = Colors.DarkRed };
        delete.Clicked += async (_, _) =>
        {
            if (list.SelectedItem is not ResourceRecord selected) return;
            await _store.DeleteAsync(selected.Id);
            await LoadAsync();
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
                new HorizontalStackLayout { Spacing = 8, Children = { save, clear, delete } },
                _status
            }
        };
        Grid.SetColumn(editor, 1);

        Content = new Grid
        {
            Padding = 20,
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(2, GridUnitType.Star))
            },
            Children =
            {
                new Border
                {
                    Stroke = Colors.LightGray,
                    Content = list,
                    Margin = new Thickness(0, 0, 12, 0)
                },
                editor
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var items = await _store.ListAsync("snippets");
        _snippets.Clear();
        foreach (var item in items) _snippets.Add(item);
        _status.Text = $"{items.Count} snippet(s)";
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _status.Text = "Informe um nome.";
            return;
        }
        await _store.CreateAsync(new(
            "snippets",
            _name.Text,
            Data: new()
            {
                ["language"] = _language.Text ?? "text",
                ["code"] = _code.Text ?? string.Empty
            }));
        _name.Text = _language.Text = _code.Text = string.Empty;
        await LoadAsync();
    }
}
