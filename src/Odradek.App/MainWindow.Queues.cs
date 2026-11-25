using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Odradek.Core.Collector;
using Odradek.Core.Data;

namespace Odradek.App;

/// <summary>Linha da subaba "Vagas do LinkedIn": em leitura, na fila ou já processada (<see cref="Outcome"/> preenchido).</summary>
public sealed record LinkedInRow(string Id, string Title, string Subtitle, ImportOutcome? Outcome, DateTime? ProcessedAt);

/// <summary>Página "Filas": as duas subabas, o selo do menu e a tela da fila de vagas do LinkedIn que a extensão alimenta.</summary>
public partial class MainWindow
{
    private const int LinkedInTabIndex = 1;

    private readonly ObservableCollection<LinkedInRow> _linkedInRows = [];
    private string _aiSummary = "";
    private long _aiActive;
    private bool _updatingLinkedIn;
    private DispatcherTimer? _linkedInTimer; // só para a contagem regressiva da espera
    private JobRecord? _focusJob; // "Ver vaga": a vaga a mostrar quando a lista de Vagas carregar

    private bool OnLinkedInTab => _page == Page.Queue && QueueTabs.SelectedIndex == LinkedInTabIndex;

    private void WireQueueTabs()
    {
        LinkedInList.ItemsSource = _linkedInRows;
        QueueTabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, QueueTabs)) return; // a seleção das listas de dentro também sobe até aqui
            ResetConfirmations();
            ShowQueueTab();
            if (!OnLinkedInTab) _ = LoadQueueAsync();
        };
        LinkedInList.SelectionChanged += (_, _) => { if (!_updatingLinkedIn) ShowLinkedInDetail(LinkedInList.SelectedItem as LinkedInRow); };
        LinkedInResumeButton.Click += (_, _) => { ResetConfirmations(); Program.LinkedIn?.SkipWait(); };
        WireConfirm(LinkedInClearButton, LinkedInClearCancel,
            () => $"{Program.LinkedIn?.ClearPending() ?? 0} vaga(s) retirada(s) da fila.",
            () => Program.LinkedIn?.Clearable ?? 0, nothing: "Nenhuma vaga aguardando na fila.");
        LinkedInOpenButton.Click += (_, _) =>
        {
            if (LinkedInList.SelectedItem is LinkedInRow row) Format.OpenInBrowser($"https://www.linkedin.com/jobs/view/{row.Id}/");
        };
        LinkedInViewJobButton.Click += (_, _) =>
        {
            if (LinkedInList.SelectedItem is LinkedInRow { Outcome.JobId: { } id }) _ = OpenImportedJobAsync(id);
        };
    }

    /// <summary>Desenha o que a subaba escolhida mostra (a lista de IA é lida à parte, por <see cref="LoadQueueAsync"/>).</summary>
    private void ShowQueueTab()
    {
        RenderLinkedInQueue();
        RenderQueueSubtitle();
    }

    private void RenderQueueSubtitle()
    {
        if (_page != Page.Queue) return;
        JobsTitle.Text = "Filas";
        var text = _aiSummary;
        if (OnLinkedInTab)
        {
            text = Program.LinkedIn?.Snapshot() is { } now
                ? $"Pendentes: {now.Pending.Count}  ·  Importadas: {now.Inserted}  ·  Já conhecidas: {now.Duplicates}  ·  Descartadas: {now.Dropped}  ·  Com falha: {now.Failed}"
                : "";
        }
        JobsSubtitle.Text = text;
        JobsSubtitle.IsVisible = text.Length > 0;
    }

    /// <summary>Selo do menu: o que ainda falta nas duas filas; cada subaba mostra o seu número.</summary>
    private void RenderQueueBadge()
    {
        long linkedIn = Program.LinkedIn?.Pending ?? 0;
        if (_queueNav is not null && _queueNav.Badge != _aiActive + linkedIn) _queueNav.Badge = _aiActive + linkedIn;
        QueueAiTab.Header = _aiActive > 0 ? $"Processamento de IA ({_aiActive})" : "Processamento de IA";
        QueueLinkedInTab.Header = linkedIn > 0 ? $"Vagas do LinkedIn ({linkedIn})" : "Vagas do LinkedIn";
    }

    // ---------- filas › vagas do LinkedIn ----------

    private static List<LinkedInRow> LinkedInRows(LinkedInQueueSnapshot now)
    {
        var rows = new List<LinkedInRow>();
        var reading = now.CurrentBatch.ToHashSet();
        foreach (var id in now.CurrentBatch) rows.Add(new LinkedInRow(id, $"Vaga {id}", "Lendo agora", null, null));
        var position = 0;
        foreach (var id in now.Pending)
            if (!reading.Contains(id)) rows.Add(new LinkedInRow(id, $"Vaga {id}", $"Na fila · posição {++position}", null, null));
        foreach (var done in now.History)
        {
            var outcome = done.Outcome;
            var what = outcome.Kind switch
            {
                ImportResult.Inserted => "Importada",
                ImportResult.Duplicate => "Já conhecida",
                ImportResult.Dropped => "Descartada — " + outcome.Detail,
                _ => "Falhou — " + outcome.Detail,
            };
            rows.Add(new LinkedInRow(outcome.Id, outcome.Title.Length > 0 ? outcome.Title : $"Vaga {outcome.Id}",
                $"{Format.Time(done.ProcessedAt)} · {what}", outcome, done.ProcessedAt));
        }
        return rows;
    }

    /// <summary>Atualiza a lista mexendo só nas linhas que mudaram (a rolagem fica onde está) e devolve a seleção à mesma vaga.</summary>
    private void RenderLinkedInQueue()
    {
        var queue = Program.LinkedIn;
        if (queue is null) return;
        var now = queue.Snapshot();
        var wanted = LinkedInRows(now);
        var selected = LinkedInList.SelectedItem as LinkedInRow;
        _updatingLinkedIn = true;
        try
        {
            for (var i = 0; i < wanted.Count; i++)
            {
                if (i == _linkedInRows.Count) _linkedInRows.Add(wanted[i]);
                else if (_linkedInRows[i] != wanted[i]) _linkedInRows[i] = wanted[i];
            }
            while (_linkedInRows.Count > wanted.Count) _linkedInRows.RemoveAt(_linkedInRows.Count - 1);
            // A mesma id pode voltar à fila depois de uma falha: primeiro procura a mesma linha, depois a vaga (que passou da fila ao histórico).
            LinkedInList.SelectedItem = selected is null ? null
                : _linkedInRows.FirstOrDefault(r => r.Id == selected.Id && r.ProcessedAt == selected.ProcessedAt) ?? _linkedInRows.FirstOrDefault(r => r.Id == selected.Id);
        }
        finally { _updatingLinkedIn = false; }
        LinkedInEmpty.IsVisible = _linkedInRows.Count == 0;
        RenderLinkedInState(now);
        ShowLinkedInDetail(LinkedInList.SelectedItem as LinkedInRow);
        RenderQueueSubtitle();
        UpdateLinkedInTimer();
    }

    private void RenderLinkedInState(LinkedInQueueSnapshot now)
    {
        var seconds = now.WaitingSeconds;
        var left = seconds < 60 ? $"{seconds} s" : $"{seconds / 60} min {seconds % 60:00} s";
        var state = now.Wait switch
        {
            LinkedInWait.RateLimit => $"Esperando {left}: o LinkedIn pediu calma. A fila continua sozinha depois.",
            LinkedInWait.Network => $"Esperando {left}: sem acesso à rede ou ao banco. A fila continua sozinha depois.",
            LinkedInWait.Pace => "Rodando (pausa curta entre os lotes).",
            _ => now.Running ? "Rodando." : now.Pending.Count > 0 ? "Parada, com vagas guardadas." : "Parada: não há vagas na fila.",
        };
        LinkedInStateText.Text = $"{state}{Environment.NewLine}{now.Pending.Count} vaga(s) por ler. Os contadores e o histórico valem desde que o Odradek foi aberto.";
        LinkedInResumeButton.IsVisible = now.Wait is LinkedInWait.RateLimit or LinkedInWait.Network;
    }

    private void ShowLinkedInDetail(LinkedInRow? row)
    {
        LinkedInOpenButton.IsVisible = row is not null;
        LinkedInViewJobButton.IsVisible = row?.Outcome?.JobId is not null;
        if (row is null) { LinkedInDetailTitle.Text = "Selecione uma vaga da fila"; LinkedInDetailBody.Text = ""; return; }
        LinkedInDetailTitle.Text = row.Title;
        var body = $"Id no LinkedIn: {row.Id}{Environment.NewLine}{row.Subtitle}";
        if (row.Outcome is null) body += Environment.NewLine + "A extensão manda só a id: o título aparece depois que a página pública da vaga é lida.";
        if (!string.Equals(LinkedInDetailBody.Text, body, StringComparison.Ordinal)) LinkedInDetailBody.Text = body; // não desfaz um texto selecionado
    }

    private void UpdateLinkedInTimer()
    {
        var counting = OnLinkedInTab && Program.LinkedIn?.Snapshot().WaitingSeconds > 0;
        if (!counting) { _linkedInTimer?.Stop(); return; }
        if (_linkedInTimer is null)
        {
            _linkedInTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _linkedInTimer.Tick += (_, _) =>
            {
                if (!OnLinkedInTab || Program.LinkedIn?.Snapshot() is not { } now) { _linkedInTimer.Stop(); return; }
                RenderLinkedInState(now);
                if (now.WaitingSeconds == 0) _linkedInTimer.Stop();
            };
        }
        if (!_linkedInTimer.IsEnabled) _linkedInTimer.Start();
    }

    // ---------- ir para a vaga importada ----------

    private async Task OpenImportedJobAsync(long id)
    {
        try
        {
            var job = await Task.Run(() => new PanelQueries(_database).Job(id));
            if (job is null) { Notify("Essa vaga não está mais no banco.", true); return; }
            _focusJob = job;
            SelectStatus(job.Status);
            _ = ReloadJobsAsync();
        }
        catch (Exception ex) { Notify($"Não foi possível abrir a vaga: {ex.Message}", true); }
    }

    /// <summary>Chamado quando a lista de Vagas carrega: seleciona a vaga pedida ou, se ela estiver em outra página, mostra só o detalhe.</summary>
    private void FocusRequestedJob()
    {
        if (_focusJob is not { } wanted || wanted.Status != _status) return;
        _focusJob = null;
        var row = _jobs.FirstOrDefault(j => j.Id == wanted.Id);
        JobsList.SelectedItem = row;
        if (row is null) ShowDetail(wanted);
        else JobsList.ScrollIntoView(row);
    }
}
