using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Odradek.Core.Extension;

namespace Odradek.App;

/// <summary>
/// "Instalar extensão": o usuário escolhe o navegador e segue os passos. Os navegadores não deixam um programa instalar uma extensão de fora
/// da loja, nem abrir a página de extensões (<c>chrome://</c> vindo de fora vira uma aba vazia). Por isso o app faz o que pode: deixa a pasta
/// pronta, abre o navegador e põe na área de transferência o endereço e, depois, o caminho da pasta.
/// </summary>
internal static class ExtensionInstallPanel
{
    public static Control Build(Action<string, bool> notify)
    {
        var panel = new StackPanel { Spacing = 8 };
        var browsers = Browsers.Installed();
        var usable = browsers.Where(b => b.Supported).ToList();
        var picker = new ComboBox { MinWidth = 240 };
        var install = new Button { Content = "Instalar extensão", Classes = { "primary" } };
        void Fill(int selected)
        {
            picker.ItemsSource = usable.Select(b => b.Name).ToList();
            picker.SelectedIndex = selected;
            install.IsEnabled = selected >= 0;
        }
        Fill(usable.Count > 0 ? 0 : -1);
        var other = new Button { Content = "Procurar outro…" };
        ToolTip.SetTip(other, "Para um navegador que não aparece na lista: escolha o programa dele (.exe).");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(picker);
        row.Children.Add(install);
        row.Children.Add(other);
        panel.Children.Add(Ui.Labeled("Navegador", row));
        if (usable.Count == 0)
            panel.Children.Add(Text("Não encontrei um navegador compatível. A extensão precisa do Chrome, do Edge ou de outro baseado no Chromium (versão 141 ou mais nova)."));
        foreach (var unsupported in browsers.Where(b => !b.Supported && b.Note.Length > 0)) panel.Children.Add(Muted($"{unsupported.Name}: {unsupported.Note}"));

        var steps = new StackPanel { Spacing = 8, IsVisible = false };
        panel.Children.Add(steps);
        picker.SelectionChanged += (_, _) => steps.IsVisible = false; // os passos são do navegador escolhido antes

        other.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(other)?.StorageProvider is not { } storage) return;
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Escolha o programa do navegador",
                    FileTypeFilter = [new FilePickerFileType("Programas") { Patterns = ["*.exe"] }],
                });
                if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
                var found = Browsers.FromFile(path);
                if (found is null) { notify("Não reconheci esse programa como um navegador compatível (Chrome, Edge, Brave, Opera, Opera GX ou Vivaldi).", true); return; }
                if (!found.Supported) { notify($"{found.Name}: {found.Note}", true); return; }
                var index = usable.FindIndex(b => string.Equals(b.Executable, found.Executable, StringComparison.OrdinalIgnoreCase));
                if (index < 0) { usable.Add(found); index = usable.Count - 1; }
                Fill(index);
            }
            catch (Exception ex)
            {
                Program.Log("Extensão: não consegui escolher o navegador: " + ex.Message);
                notify("Não foi possível escolher o programa.", true);
            }
        };

        install.Click += async (_, _) =>
        {
            if (picker.SelectedIndex < 0) return;
            var browser = usable[picker.SelectedIndex];
            install.IsEnabled = false;
            try
            {
                var folder = Program.ExtensionFolder;
                if (!await Task.Run(folder.Sync)) { notify("Não consegui preparar a pasta da extensão. Veja desktop.log na pasta de dados.", true); return; }
                ShowSteps(steps, browser, folder.Target, notify);
                var copied = await CopyAsync(install, browser.ExtensionsPage);
                var opened = Open(browser);
                notify((opened ? $"{browser.Name} aberto. " : $"Abra o {browser.Name}. ")
                    + (copied ? "Endereço copiado: cole na barra de endereço." : "Copie o endereço do passo 1."), !opened);
            }
            finally { install.IsEnabled = true; }
        };
        return panel;
    }

    private static void ShowSteps(StackPanel steps, BrowserInfo browser, string folder, Action<string, bool> notify)
    {
        steps.Children.Clear();
        if (!browser.Tested)
            steps.Children.Add(Text($"Atenção: a extensão ainda não foi testada no {browser.Name}, e o painel lateral dela pode não abrir. Se não abrir, use o Chrome ou o Edge."));
        steps.Children.Add(Text($"1. No {browser.Name}, clique na barra de endereço, cole (Ctrl+V) e tecle Enter. O endereço é {browser.ExtensionsPage}"));
        steps.Children.Add(CopyButton("Copiar endereço", browser.ExtensionsPage, "Endereço copiado.", notify));
        steps.Children.Add(Text($"2. Ligue o “{browser.DeveloperModeLabel}” (Developer mode)."));
        steps.Children.Add(Text($"3. Clique em “{browser.LoadButtonLabel}” (Load unpacked). Na janela que abrir, cole o caminho no campo “Pasta” e confirme."));
        steps.Children.Add(CopyButton("Copiar caminho da pasta", folder, "Caminho da pasta copiado.", notify));
        steps.Children.Add(new SelectableTextBlock { Text = folder, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap });
        steps.Children.Add(Text("4. Fixe o ícone do Odradek na barra do navegador, abra o painel e clique em “Connect to Odradek”. Aqui no app, permita a conexão."));
        if (browser.Note.Length > 0) steps.Children.Add(Muted(browser.Note));
        steps.Children.Add(Muted("Não apague nem mova essa pasta: o navegador lê a extensão dali, e o Odradek a atualiza sozinho."));
        steps.IsVisible = true;
    }

    private static bool Open(BrowserInfo browser)
    {
        try
        {
            var start = new ProcessStartInfo(browser.Executable) { UseShellExecute = false };
            start.ArgumentList.Add("--new-window");
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Program.Log($"Extensão: não consegui abrir {browser.Executable}: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> CopyAsync(Control from, string value)
    {
        try
        {
            if (TopLevel.GetTopLevel(from)?.Clipboard is not { } clipboard) return false;
            await clipboard.SetTextAsync(value);
            return true;
        }
        catch (Exception ex)
        {
            Program.Log("Extensão: não consegui copiar: " + ex.Message);
            return false;
        }
    }

    private static Button CopyButton(string label, string value, string done, Action<string, bool> notify)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 0, 0, 0) };
        button.Click += async (_, _) =>
        {
            var copied = await CopyAsync(button, value);
            notify(copied ? done : "Não foi possível acessar a área de transferência.", !copied);
        };
        return button;
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
}
