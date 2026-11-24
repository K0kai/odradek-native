using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Odradek.App;

/// <summary>
/// Aviso curto no canto da tela, sem o foco e sem pacote extra: faz o papel do balão da bandeja do app Python ("o Odradek continua na bandeja",
/// "Backup concluído"). Fica 7 s; um clique fecha. Só um por vez.
/// </summary>
internal static class Toast
{
    private static Window? _current;
    internal static Window? Current => _current;

    public static void Show(string text) => Dispatcher.UIThread.Post(() =>
    {
        try { Open(text); }
        catch (Exception ex) { Program.Log("Não foi possível mostrar o aviso: " + ex.Message); }
    });

    private static void Open(string text)
    {
        _current?.Close();
        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = 380,
            Opacity = 0,
            Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) } },
            Background = new SolidColorBrush(Color.Parse(dark ? "#2B2B2B" : "#F7F7F7")),
            Content = new Border
            {
                Padding = new Thickness(16, 12),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#55808080")),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = "Odradek", FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left },
                    },
                },
            },
        };
        // Ao abrir, o tamanho ainda é o provisório (380×714); o real só vem depois do layout. Reposiciona a cada mudança de tamanho
        // para o canto de baixo ficar ancorado no canto inferior direito da área útil.
        window.Opened += (_, _) => Place(window);
        window.SizeChanged += (_, _) => Place(window);
        window.PointerPressed += (_, _) => window.Close();
        window.Closed += (_, _) => { if (ReferenceEquals(_current, window)) _current = null; };
        _current = window;
        window.Show();
        Dispatcher.UIThread.Post(() => window.Opacity = 1, DispatcherPriority.Background);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        timer.Tick += (_, _) => { timer.Stop(); if (window.IsVisible) window.Close(); };
        timer.Start();
    }

    private static void Place(Window window)
    {
        var screen = window.Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(window.Bounds.Width * scale);
        var height = (int)(window.Bounds.Height * scale);
        var margin = (int)(16 * scale);
        window.Position = new PixelPoint(area.Right - width - margin, area.Bottom - height - margin);
    }
}
