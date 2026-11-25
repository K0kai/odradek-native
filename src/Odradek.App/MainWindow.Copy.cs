using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Odradek.Core.Data;

namespace Odradek.App;

/// <summary>Copiar para a área de transferência: linhas de log (as selecionadas ou o log inteiro) e a vaga aberta.</summary>
public partial class MainWindow
{
    private void WireCopy()
    {
        LogsCopyButton.Click += async (_, _) => await CopyWholeLogAsync();
        CopyJobButton.Click += async (_, _) => await CopyJobAsync(_detailJob);

        OnCopyKey(LogsList, () => _ = CopyLogLinesAsync(SelectedLogs()));
        OnCopyKey(JobsList, () => _ = CopyJobAsync(JobsList.SelectedItem as JobRecord));

        SelectRowsThroughText(LogsList);

        // botão direito: a linha clicada; se ela faz parte da seleção, a seleção inteira
        OnRowMenu<LogEntry>(LogsList, "Copiar linha", entry =>
        {
            var selected = SelectedLogs();
            _ = CopyLogLinesAsync(selected.Contains(entry) ? selected : [entry]);
        });
        OnRowMenu<JobRecord>(JobsList, "Copiar vaga", job => _ = CopyJobAsync(job));
    }

    /// <summary>Ctrl+C na lista. Com um trecho de texto selecionado na linha, quem copia é o próprio texto.</summary>
    private static void OnCopyKey(ListBox list, Action copy) => list.AddHandler(InputElement.KeyDownEvent, (_, e) =>
    {
        if (e.Key != Key.C || e.KeyModifiers != KeyModifiers.Control) return;
        if (e.Source is SelectableTextBlock { SelectedText.Length: > 0 }) return;
        e.Handled = true;
        copy();
    }, RoutingStrategies.Tunnel);

    /// <summary>
    /// O texto selecionável consome o clique (para marcar o trecho) e a lista não fica sabendo: aqui a linha é selecionada mesmo assim,
    /// com Ctrl (soma ou tira) e Shift (intervalo). O clique fora do texto continua com a própria lista.
    /// </summary>
    private static void SelectRowsThroughText(ListBox list) => list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
    {
        if (e.Source is not SelectableTextBlock text || text.FindAncestorOfType<ListBoxItem>() is not { } row) return;
        if (!e.GetCurrentPoint(text).Properties.IsLeftButtonPressed) return;
        var index = list.IndexFromContainer(row);
        if (index < 0) return;
        var selection = list.Selection;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && selection.AnchorIndex >= 0)
        {
            var anchor = selection.AnchorIndex;
            selection.BeginBatchUpdate();
            selection.Clear();
            selection.SelectRange(anchor, index);
            selection.AnchorIndex = anchor;
            selection.EndBatchUpdate();
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (selection.IsSelected(index)) selection.Deselect(index); else selection.Select(index);
        }
        else list.SelectedIndex = index;
    }, RoutingStrategies.Bubble, handledEventsToo: true);

    /// <summary>Menu do botão direito sobre a linha; com um trecho marcado no texto clicado, oferece também copiar só o trecho.</summary>
    private void OnRowMenu<T>(ListBox list, string label, Action<T> copy) where T : class
    {
        T? target = null;
        var excerpt = "";
        var excerptItem = new MenuItem { Header = "Copiar trecho selecionado" };
        excerptItem.Click += async (_, _) => await Ui.CopyAsync(this, excerpt);
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => { if (target is not null) copy(target); };
        list.ContextMenu = new ContextMenu { ItemsSource = new[] { excerptItem, item } };
        list.AddHandler(Control.ContextRequestedEvent, (_, e) =>
        {
            excerpt = (e.Source as SelectableTextBlock)?.SelectedText ?? "";
            excerptItem.IsVisible = excerpt.Length > 0;
            target = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as T;
            if (target is null) e.Handled = true; // fora de uma linha não há o que copiar
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>As linhas selecionadas, na ordem em que aparecem na tela.</summary>
    private List<LogEntry> SelectedLogs()
    {
        var picked = LogsList.SelectedItems?.OfType<LogEntry>().ToHashSet() ?? [];
        return _logs.Where(picked.Contains).ToList();
    }

    private async Task CopyLogLinesAsync(IReadOnlyList<LogEntry> entries)
    {
        if (entries.Count == 0) return;
        var text = string.Join(Environment.NewLine, entries.Select(Format.LogLine));
        if (await Ui.CopyAsync(this, text)) Notify(entries.Count == 1 ? "1 linha copiada." : $"{entries.Count} linhas copiadas.", false);
        else Notify("Não foi possível acessar a área de transferência.", true);
    }

    /// <summary>Tudo o que está guardado (a tela mostra só as 200 mais novas), da linha mais antiga para a mais nova.</summary>
    private async Task CopyWholeLogAsync()
    {
        try
        {
            var entries = await Task.Run(() => new EventLogStore(_database).List(EventLogStore.MaxRows).Reverse().ToList());
            if (entries.Count == 0) Notify("Nenhum registro para copiar.", false);
            else await CopyLogLinesAsync(entries);
        }
        catch (Exception ex) { Notify($"Não foi possível ler o log: {ex.Message}", true); }
    }

    private async Task CopyJobAsync(JobRecord? job)
    {
        if (job is null) return;
        if (await Ui.CopyAsync(this, Format.Job(job))) Notify("Vaga copiada.", false);
        else Notify("Não foi possível acessar a área de transferência.", true);
    }
}
