using Avalonia.Controls;
using Avalonia.Input;

namespace Odradek.App;

public partial class MainWindow
{
    /// <summary>
    /// A barra de título do Windows foi removida (<c>WindowDecorations="BorderOnly"</c>: sem título, mas com a borda que redimensiona);
    /// esta é a do app. Arrastar a faixa do topo move a janela, duplo clique maximiza/restaura, e os três botões fazem o de sempre
    /// (fechar só esconde na bandeja, como o X do Windows fazia).
    /// </summary>
    private void WireChrome()
    {
        void Drag(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) ToggleMaximize();
            else BeginMoveDrag(e);
        }
        TitleDrag.PointerPressed += Drag;
        SidebarDrag.PointerPressed += Drag;
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Close();
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) ShowMaximizeGlyph(); };
        ShowMaximizeGlyph();
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Maximizada, o botão do meio vira "restaurar" (dois quadrados).</summary>
    private void ShowMaximizeGlyph()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxButton.Content = maximized ? "" : "";
        ToolTip.SetTip(MaxButton, maximized ? "Restaurar" : "Maximizar");
    }
}
