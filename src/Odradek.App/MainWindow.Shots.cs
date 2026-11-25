using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;

namespace Odradek.App;

public partial class MainWindow
{
    /// <summary>Capturas das telas (ODRADEK_SHOTS=pasta): uma PNG por tela, para conferir o visual sem tirar print à mão.</summary>
    private async Task ShotsAsync(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            await Task.Delay(2500);
            foreach (var (page, label) in Pages)
            {
                NavList.SelectedIndex = Array.FindIndex(Pages, p => p.Page == page);
                await Task.Delay(1200);
                if (page == Page.Jobs)
                {
                    foreach (var key in new[] { "ignored", "applied" })
                    {
                        SelectStatus(key); await Task.Delay(1200);
                        JobsList.SelectedIndex = _jobs.Count > 0 ? 0 : -1; await Task.Delay(500);
                        Save(Path.Combine(folder, $"0-Jobs-{key}.png"));
                        // o detalhe é longo: mais duas capturas, rolando até o meio e o fim
                        if (DetailTitle.FindAncestorOfType<ScrollViewer>() is { } scroll && scroll.Extent.Height > scroll.Viewport.Height)
                            for (var part = 1; part <= 2; part++)
                            {
                                scroll.Offset = new Vector(0, Math.Min(scroll.Extent.Height - scroll.Viewport.Height, scroll.Viewport.Height * 0.9 * part)); await Task.Delay(400);
                                Save(Path.Combine(folder, $"0-Jobs-{key}-rolagem{part}.png"));
                            }
                    }
                    SelectStatus("worth"); await Task.Delay(800);
                }
                if (page == Page.Queue) { QueueList.SelectedIndex = _queueRows.Count > 0 ? 0 : -1; await Task.Delay(900); }
                if (page == Page.Assistant && AssistantHost.Children.Contains(AssistantEmpty))
                {
                    AssistantHost.Children.Remove(AssistantEmpty);
                    AssistantHost.Children.Add(Bubble("user", "Quantos anos de experiência você tem com Python?"));
                    AssistantHost.Children.Add(Bubble("assistant", "Tenho cerca de 2 anos de experiência prática com Python, em automações e integrações com APIs.")); await Task.Delay(300);
                }
                Save(Path.Combine(folder, $"{(int)page}-{page}.png"));
                if (page == Page.Queue)
                {
                    QueueTabs.SelectedIndex = LinkedInTabIndex; await Task.Delay(600);
                    LinkedInList.SelectedIndex = _linkedInRows.Count > 0 ? 0 : -1; await Task.Delay(500);
                    Save(Path.Combine(folder, $"{(int)page}-{page}-linkedin.png"));
                    QueueTabs.SelectedIndex = 0;
                }
                if (page == Page.Settings)
                    for (var tab = 1; tab < SettingsTabs.ItemCount; tab++)
                    {
                        SettingsTabs.SelectedIndex = tab; await Task.Delay(500);
                        Save(Path.Combine(folder, $"{(int)page}-{page}-{tab}.png"));
                    }
            }
            NavList.SelectedIndex = Array.FindIndex(Pages, p => p.Page == Page.Jobs); Width = 1280; Height = 860; ThemeBox.SelectedIndex = 1; await Task.Delay(1000);
            Save(Path.Combine(folder, "11-Tema-claro.png"));
            ThemeBox.SelectedIndex = 2; await Task.Delay(1000);
            Save(Path.Combine(folder, "12-Tema-escuro.png"));
            ThemeBox.SelectedIndex = 3; await Task.Delay(1000);
            Save(Path.Combine(folder, "13-Tema-dracula.png"));
            Toast.Show("O Odradek continua rodando na bandeja. Use Sair no menu do ícone para encerrar."); await Task.Delay(1200);
            if (Toast.Current is { } toast)
            {
                Program.Log($"SHOTS toast: posição {toast.Position}, tamanho {toast.Bounds.Width}x{toast.Bounds.Height}, área útil {Screens.Primary?.WorkingArea}");
                using var shot = new RenderTargetBitmap(new PixelSize((int)toast.Bounds.Width, (int)toast.Bounds.Height), new Vector(96, 96));
                shot.Render(toast); shot.Save(Path.Combine(folder, "10-Toast.png"));
            }
            NavList.SelectedIndex = Array.FindIndex(Pages, p => p.Page == Page.Jobs); Width = 900; Height = 600; await Task.Delay(1200);
            Save(Path.Combine(folder, "9-Jobs-minimo.png"));
        }
        catch (Exception ex) { Program.Log("SHOTS erro: " + ex); }
        if (Avalonia.Application.Current is App app) await app.QuitAsync();
    }

    private void Save(string path)
    {
        var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(this);
        bitmap.Save(path);
    }
}
