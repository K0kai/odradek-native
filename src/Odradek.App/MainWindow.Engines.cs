using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Odradek.Core;
using Odradek.Core.Actions;
using Odradek.Core.Engines;

namespace Odradek.App;

/// <summary>Fase 4 na janela: o botão da coleta, o Assistente e as cotas ao vivo. Os motores são montados só quando alguém os usa.</summary>
public partial class MainWindow
{
    private readonly JsonArray _assistantHistory = [];
    private bool _assistantBusy, _usageLoading;

    private void WireEngines()
    {
        CollectorButton.Click += async (_, _) => await ToggleCollectorAsync();
        AssistantSendButton.Click += async (_, _) => await AskAssistantAsync();
        AssistantClearButton.Click += (_, _) => ResetAssistant();
        AssistantInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; await AskAssistantAsync(); }
        };
        // Cotas externas: a tela de uso se atualiza sozinha (o serviço decide se consulta de verdade: 15 s com coleta, 5 min parado).
        var usageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        usageTimer.Tick += (_, _) => { if (_page == Page.Usage) _ = LoadUsageAsync(); };
        usageTimer.Start();
    }

    // ---------- coleta ----------

    private async Task ToggleCollectorAsync()
    {
        CollectorButton.IsEnabled = false;
        try
        {
            var message = await Task.Run(() =>
            {
                var collector = Program.Engines.Collector;
                if (collector.Snapshot().State == "running") { collector.Stop(); return "Parada solicitada; a coleta termina a busca atual."; }
                return collector.Start() ? "Coleta iniciada." : "A coleta já está em andamento.";
            });
            Notify(message, false);
        }
        catch (Exception ex) { Notify($"Não foi possível alterar a coleta: {ex.Message}", true); }
        finally { RefreshCollector(); }
    }

    /// <summary>Texto e botão da coleta, a partir do estado do motor (atualiza a cada <c>CollectorChanged</c>).</summary>
    private void RefreshCollector()
    {
        if (!Program.EnginesCreated) return;
        var s = Program.Engines.Collector.Snapshot();
        var lines = new List<string> { s.Message };
        if (s.State == "running" && s.NextRunAt is { Length: > 0 } next) lines.Add($"Próxima busca: {Format.Date(next, true)}");
        if (s.LastRun is { Length: > 0 } last) lines.Add($"Última: {Format.Date(last, true)} · {s.LastFound} vaga(s)");
        CollectorText.Text = string.Join(Environment.NewLine, lines);
        CollectorButton.Content = s.State switch { "running" => "Parar coleta", "stopping" => "Parando…", _ => "Iniciar coleta" };
        CollectorButton.IsEnabled = s.State != "stopping";
    }

    // ---------- assistente ----------

    private void ResetAssistant()
    {
        if (_assistantBusy) return;
        _assistantHistory.Clear();
        AssistantHost.Children.Clear();
        AssistantHost.Children.Add(AssistantEmpty);
        AssistantState.Text = "";
    }

    private Border Bubble(string role, string text)
    {
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new TextBlock { Text = role == "user" ? "Você" : "Odradek", Classes = { "muted" }, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        if (role == "assistant")
        {
            var copy = Ui.Button("Copiar resposta", () => { });
            copy.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) { await clipboard.SetTextAsync(text); Notify("Resposta copiada.", false); }
            };
            copy.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(copy);
        }
        var bubble = Ui.Card(body);
        if (role == "user") bubble.Background = Palette.Brush("AccentSoft");
        bubble.Margin = role == "user" ? new Avalonia.Thickness(48, 0, 0, 0) : new Avalonia.Thickness(0, 0, 48, 0);
        return bubble;
    }

    private async Task AskAssistantAsync()
    {
        if (_assistantBusy) return;
        var question = (AssistantInput.Text ?? "").Trim();
        if (question.Length == 0) { Notify("Cole a pergunta do formulário.", true); return; }
        _assistantBusy = true;
        AssistantSendButton.IsEnabled = AssistantClearButton.IsEnabled = false;
        AssistantState.Text = "Gerando resposta…";
        AssistantHost.Children.Remove(AssistantEmpty);
        AssistantHost.Children.Add(Bubble("user", question));
        AssistantScroll.ScrollToEnd();
        var history = JsonNode.Parse(_assistantHistory.ToJsonString()); // o que veio antes desta pergunta
        try
        {
            var answer = await Task.Run(() => Program.Engines.Assistant.AnswerAsync(question, history));
            _assistantHistory.Add(new JsonObject { ["role"] = "user", ["content"] = question });
            _assistantHistory.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer });
            AssistantHost.Children.Add(Bubble("assistant", answer));
            AssistantInput.Text = "";
            AssistantState.Text = "";
        }
        catch (ActionException ex) { Notify(ex.Message, true); AssistantState.Text = ex.Message; }
        catch (Exception ex) { Notify(ex.Message, true); AssistantState.Text = ex.Message; }
        finally
        {
            _assistantBusy = false;
            AssistantSendButton.IsEnabled = AssistantClearButton.IsEnabled = true;
            AssistantScroll.ScrollToEnd();
        }
    }

    // ---------- cotas ----------

    private static string QuotaText(System.Text.Json.Nodes.JsonObject snapshot, out double percent)
    {
        percent = 0;
        if (snapshot["configured"]?.GetValue<bool>() != true)
            return "Chave não configurada. Salve a chave em Configurações → Contas e chaves para acompanhar a cota dos modelos gratuitos.";
        if (Text.Truthy(snapshot["error"])) return Text.PyStr(snapshot["error"]);
        var onPlan = Text.Truthy(snapshot["on_plan"]);
        percent = Convert.ToDouble(Text.PyStrRaw(snapshot[onPlan ? "plan_used_pct" : "used_pct"]), System.Globalization.CultureInfo.InvariantCulture);
        var exhausted = Text.Truthy(snapshot[onPlan ? "plan_exhausted" : "exhausted"]);
        var plan = Text.PyStr(snapshot["plan_name"]);
        var lines = new List<string> { $"{(plan.Length > 0 ? $"Plano {plan} · " : "")}{percent:0}% usado{(exhausted ? " · esgotado" : "")}" };
        if (Text.PyStr(snapshot["reset_at"]) is { Length: > 0 } reset) lines.Add($"Renova em {Format.Date(reset, true)}");
        var models = (snapshot["models"] as JsonArray)?.Count ?? 0;
        lines.Add(snapshot["models_enabled"]?.GetValue<bool>() == false ? "Modelos gratuitos desativados no plano." : $"{models} modelo(s) gratuito(s) disponível(is).");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Consulta as cotas externas (com o cache do serviço) e, se algo mudou, redesenha a tela de uso.</summary>
    private async Task RefreshQuotasAsync()
    {
        string harbor; double harborPercent;
        try
        {
            var quotas = Program.Engines.Quotas;
            var running = Program.Engines.Collector.Snapshot().State != "stopped";
            await Task.Run(async () => await quotas.FreshApifyAsync(running));
            harbor = QuotaText(await quotas.TokenHarborSnapshotAsync(), out harborPercent);
        }
        catch (Exception ex) { harbor = $"Não foi possível consultar a cota: {ex.Message}"; harborPercent = 0; }
        TokenHarborText.Text = harbor;
        TokenHarborBar.Value = harborPercent;
        TokenHarborBar.Foreground = Palette.Brush(Palette.QuotaBrushKey(harborPercent));
        TokenHarborBar.IsVisible = harborPercent > 0;
    }
}
