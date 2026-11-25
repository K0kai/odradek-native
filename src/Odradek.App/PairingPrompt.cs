using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Odradek.Core.Actions;

namespace Odradek.App;

/// <summary>A pergunta do "Conectar ao Odradek": sem um "Permitir" aqui, a extensão não recebe token. Fechar a janela é recusar.</summary>
internal static class PairingPrompt
{
    /// <summary>Chamável de qualquer thread. Cancelar o <paramref name="ct"/> (tempo esgotado ou a extensão desistiu) fecha a pergunta.</summary>
    public static async Task<bool> AskAsync(CancellationToken ct)
    {
        var replacing = await Task.Run(() => new ExtensionTokenActions(Program.Database).Status().Configured, ct);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? window = null;
        await using var registration = ct.Register(() =>
        {
            answer.TrySetCanceled(ct);
            Dispatcher.UIThread.Post(() => window?.Close());
        });
        Dispatcher.UIThread.Post(() =>
        {
            if (answer.Task.IsCompleted) return;
            try
            {
                (Application.Current as App)?.ShowWindow();
                window = Create(replacing, allowed => { answer.TrySetResult(allowed); window?.Close(); });
                window.Closed += (_, _) => answer.TrySetResult(false);
                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                Program.Log("Extensão: não consegui mostrar o pedido de conexão: " + ex);
                answer.TrySetResult(false);
            }
        });
        return await answer.Task;
    }

    private static Window Create(bool replacing, Action<bool> respond)
    {
        var allow = new Button { Content = "Permitir", Classes = { "primary" } };
        var refuse = new Button { Content = "Recusar", IsCancel = true };
        allow.Click += (_, _) => respond(true);
        refuse.Click += (_, _) => respond(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(refuse);
        buttons.Children.Add(allow);

        var content = new StackPanel { Spacing = 12, Margin = new Thickness(20), Width = 380 };
        content.Children.Add(new TextBlock { Text = "A extensão Odradek Autofill quer se conectar a este app. Permitir?", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = "Conectada, ela lê o seu perfil e currículo, envia vagas e usa o assistente. Só permita se você acabou de clicar em “Connect to Odradek”.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (replacing)
            content.Children.Add(new TextBlock { Text = "Já existe uma extensão conectada (ou um token gerado): ela será desconectada.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(buttons);

        return new Window
        {
            Title = "Odradek: conectar a extensão",
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
    }
}
