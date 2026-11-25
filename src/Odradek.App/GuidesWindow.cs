using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Odradek.App;

/// <summary>
/// "Guias": o passo a passo das configurações que dependem de sites de fora ou de uma ordem. Não é modal, para a pessoa seguir o guia
/// enquanto preenche as Configurações; há uma só, e abrir outro guia reaproveita a janela.
/// </summary>
internal sealed class GuidesWindow : Window
{
    private static GuidesWindow? _current;

    private readonly ListBox _list = new() { Classes = { "nav" }, Margin = new Thickness(8, 12) };
    private readonly ScrollViewer _page = new();

    /// <summary>A rolagem da página do guia (as capturas de tela a usam).</summary>
    internal ScrollViewer Page => _page;

    private GuidesWindow()
    {
        Title = "Odradek: guias de configuração";
        Width = 920;
        Height = 680;
        MinWidth = 660;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.ItemsSource = Guides.All.Select(g => g.Title).ToList();
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            _page.Content = Render(Guides.All[_list.SelectedIndex]);
            _page.Offset = default;
        };

        var side = new Border { Child = _list, BorderThickness = new Thickness(0, 0, 1, 0) };
        side.Bind(Border.BorderBrushProperty, side.GetResourceObservable("CardBorder"));
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("232,*") };
        Grid.SetColumn(_page, 1);
        root.Children.Add(side);
        root.Children.Add(_page);
        Content = root;
    }

    /// <summary>Sem <paramref name="guideId"/> (o botão da barra de título): abre no começo, ou traz a janela já aberta sem trocar de guia.</summary>
    public static GuidesWindow Show(Window? owner, string? guideId)
    {
        if (_current is null)
        {
            _current = new GuidesWindow();
            _current.Closed += (_, _) => _current = null;
            _current.Select(guideId ?? Guides.Start);
            if (owner is null) _current.Show();
            else _current.Show(owner);
        }
        else
        {
            if (guideId is not null) _current.Select(guideId);
            if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
        }
        _current.Activate();
        return _current;
    }

    private void Select(string guideId) => _list.SelectedIndex = Array.IndexOf(Guides.All, Guides.Get(guideId));

    private Control Render(Guide guide)
    {
        var page = new StackPanel { Spacing = 12, Margin = new Thickness(20, 16, 20, 24), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(new TextBlock { Text = guide.Title, Classes = { "h1" }, TextWrapping = TextWrapping.Wrap });
        page.Children.Add(Body(guide.Intro));
        foreach (var warning in guide.Warnings) page.Children.Add(Warning(warning));
        for (var i = 0; i < guide.Steps.Length; i++) page.Children.Add(Step(i + 1, guide.Steps[i]));
        if (guide.Troubleshooting.Length > 0)
        {
            var list = new StackPanel { Spacing = 10 };
            list.Children.Add(new TextBlock { Text = "Se der errado", Classes = { "h2" } });
            foreach (var (problem, fix) in guide.Troubleshooting)
            {
                var item = new StackPanel { Spacing = 2 };
                item.Children.Add(new SelectableTextBlock { Text = problem, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                item.Children.Add(Body(fix));
                list.Children.Add(item);
            }
            page.Children.Add(Ui.Card(list));
        }
        return page;
    }

    private static SelectableTextBlock Body(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static Border Warning(string text)
    {
        var content = new StackPanel { Spacing = 2 };
        var title = new TextBlock { Text = "Atenção", FontWeight = FontWeight.SemiBold };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("Accent"));
        content.Children.Add(title);
        content.Children.Add(Body(text));
        var box = new Border { Child = content, Classes = { "notice" } };
        box.BorderThickness = new Thickness(3, 1, 1, 1);
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("Accent"));
        return box;
    }

    private Control Step(int number, GuideStep step)
    {
        var badgeText = new TextBlock { Text = number.ToString(), FontSize = 12, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border { Child = badgeText, Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
        Ui.Paint(badge, badgeText, "AccentSoft", "Accent");

        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock { Text = step.Title, Classes = { "h2" }, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(Body(step.Text));
        var actions = new WrapPanel();
        void Add(Button button) { button.Margin = new Thickness(0, 2, 8, 0); actions.Children.Add(button); }
        if (step.Link is { } link) Add(LinkButton(link.Label, link.Url));
        if (step.Copy is { } copy) Add(CopyButton(copy.Label, copy.Value));
        if (step.Next.Length > 0) Add(Ui.Button($"Ver o guia: {Guides.Get(step.Next).Title}", () => Select(step.Next)));
        if (actions.Children.Count > 0) content.Children.Add(actions);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(content, 1);
        row.Children.Add(badge);
        row.Children.Add(content);
        return Ui.Card(row);
    }

    private static Button LinkButton(string label, string url)
    {
        var button = Ui.Button(label + " ↗", () => Format.OpenInBrowser(url));
        ToolTip.SetTip(button, url); // o endereço fica à vista antes do clique (e serve se o navegador não abrir)
        return button;
    }

    private static Button CopyButton(string label, string value)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            button.Content = await Ui.CopyAsync(button, value) ? "Copiado ✓" : "Não consegui copiar";
            DispatcherTimer.RunOnce(() => button.Content = label, TimeSpan.FromSeconds(2));
        };
        return button;
    }
}
