using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Odradek.App;

public partial class MainWindow
{
    /// <summary>Atalhos de teclado e o fundo Mica do Windows 11 (com volta ao fundo liso quando o sistema não oferece).</summary>
    private void WirePolish()
    {
        KeyDown += OnShortcut;
        WireTheme();
        SizeChanged += (_, e) => LayoutHeader(e.NewSize.Width);
        if (Environment.GetEnvironmentVariable("ODRADEK_SHOTS") is { Length: > 0 }) return; // a captura da tela transparente sai manchada: nos testes o fundo é liso
        TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
        ActualThemeVariantChanged += (_, _) => ApplyBackdrop();
        Opened += (_, _) => ApplyBackdrop();
        PropertyChanged += (_, e) => { if (e.Property == TopLevel.ActualTransparencyLevelProperty) ApplyBackdrop(); };
    }

    /// <summary>A tela recém-aberta começa transparente e volta à opacidade total (a transição de 0,18 s fica em App.axaml).</summary>
    private static void FadeIn(Control pane)
    {
        pane.Opacity = 0;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => pane.Opacity = 1, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ---------- tema ----------

    private static readonly (string Key, string Label, Avalonia.Styling.ThemeVariant Variant)[] Themes =
    [
        ("system", "Igual ao Windows", Avalonia.Styling.ThemeVariant.Default),
        ("light", "Claro", Avalonia.Styling.ThemeVariant.Light),
        ("dark", "Escuro", Avalonia.Styling.ThemeVariant.Dark),
    ];

    /// <summary>Seletor de tema na lateral; a escolha fica em <c>ui_theme</c> (tabela settings). <c>ODRADEK_THEME</c> (testes) tem prioridade.</summary>
    private void WireTheme()
    {
        ThemeBox.ItemsSource = Themes.Select(t => t.Label).ToList();
        var saved = "system";
        try { saved = new Odradek.Core.Data.SettingsStore(_database).Get("ui_theme", "system"); } catch (Exception ex) { Program.Log("Tema salvo ilegível: " + ex.Message); }
        var index = Math.Max(0, Array.FindIndex(Themes, t => t.Key == saved));
        ThemeBox.SelectedIndex = index;
        if (Environment.GetEnvironmentVariable("ODRADEK_THEME") is not { Length: > 0 } && Application.Current is { } app) app.RequestedThemeVariant = Themes[index].Variant;
        ThemeBox.SelectionChanged += (_, _) =>
        {
            if (ThemeBox.SelectedIndex < 0) return;
            var theme = Themes[ThemeBox.SelectedIndex];
            if (Application.Current is { } current) current.RequestedThemeVariant = theme.Variant;
            _ = Task.Run(() =>
            {
                try { new Odradek.Core.Data.SettingsStore(_database).Set("ui_theme", theme.Key); }
                catch (Exception ex) { Program.Log("Não foi possível salvar o tema: " + ex.Message); }
            });
        };
    }

    /// <summary>Janela estreita: os controles da lista de vagas descem para uma segunda linha, em vez de espremer o título.</summary>
    private void LayoutHeader(double width)
    {
        var narrow = width < 1200;
        Grid.SetRow(JobsControls, narrow ? 1 : 0);
        Grid.SetColumn(JobsControls, narrow ? 0 : 1);
        Grid.SetColumnSpan(JobsControls, narrow ? 2 : 1);
        JobsControls.Margin = narrow ? new Thickness(0, 10, 0, 0) : default;
    }

    /// <summary>Com Mica a janela fica transparente e o conteúdo ganha um véu do fundo do app; sem Mica volta tudo ao fundo liso.</summary>
    private void ApplyBackdrop()
    {
        var mica = ActualTransparencyLevel == WindowTransparencyLevel.Mica;
        if (!mica)
        {
            ClearValue(BackgroundProperty);
            Sidebar.Background = Palette.Brush("SidebarBg");
            ContentHost.Background = Brushes.Transparent;
            return;
        }
        Background = Brushes.Transparent;
        Sidebar.Background = Veil("SidebarBg", 0.55);
        ContentHost.Background = Veil("AppBg", 0.72);
    }

    private static IBrush Veil(string key, double opacity) =>
        Palette.Brush(key) is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : Palette.Brush(key);

    /// <summary>Ctrl+1…8 abre a tela do menu, F5 relê a tela atual, Ctrl+F vai para a busca de vagas.</summary>
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is >= Key.D1 and <= Key.D8)
        {
            var index = e.Key - Key.D1;
            if (index < Pages.Length) { NavList.SelectedIndex = index; e.Handled = true; }
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.F5) { Navigate(_page); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F)
        {
            NavList.SelectedIndex = 0;
            SearchBox.Focus();
            e.Handled = true;
        }
    }
}
