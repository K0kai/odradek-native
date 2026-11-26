using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Odradek.Core.Collector;

namespace Odradek.App;

/// <summary>Filas › Vagas do Indeed: o andamento do envio que a extensão faz. Aqui o app só acompanha; quem lê as vagas é a aba do usuário.</summary>
public partial class MainWindow
{
    private const int IndeedTabIndex = 2;

    private readonly ObservableCollection<LinkedInRow> _indeedRows = [];
    private bool _updatingIndeed;
    private DispatcherTimer? _indeedTimer; // para perceber que a extensão parou de dar notícia

    private bool OnIndeedTab => _page == Page.Queue && QueueTabs.SelectedIndex == IndeedTabIndex;

    private void WireIndeedTab()
    {
        IndeedList.ItemsSource = _indeedRows;
        IndeedList.SelectionChanged += (_, _) => { if (!_updatingIndeed) ShowIndeedDetail(IndeedList.SelectedItem as LinkedInRow); };
        WireConfirm(IndeedClearButton, IndeedClearCancel,
            () => $"{Program.Indeed?.ClearPending() ?? 0} vaga(s) retirada(s) da fila.",
            () => Program.Indeed?.Pending ?? 0, nothing: "Nenhuma vaga aguardando na fila.");
        IndeedOpenButton.Click += (_, _) => { if (IndeedList.SelectedItem is LinkedInRow row) Format.OpenInBrowser(row.Url); };
        IndeedViewJobButton.Click += (_, _) =>
        {
            if (IndeedList.SelectedItem is LinkedInRow { Outcome.JobId: { } id }) _ = OpenImportedJobAsync(id);
        };
    }

    private static string IndeedUrl(string host, string id) => $"https://{host}/viewjob?jk={id}";

    private static List<LinkedInRow> IndeedRows(IndeedQueueSnapshot now)
    {
        var rows = new List<LinkedInRow>();
        var reading = now.CurrentBatch.Count;
        for (var i = 0; i < now.Pending.Count; i++)
        {
            var job = now.Pending[i];
            var what = i < reading ? "No lote em leitura"
                : now.State == IndeedState.Receiving ? $"Na fila · posição {i - reading + 1}"
                : "Não lida · envie de novo pela extensão";
            var who = string.Join(" · ", new[] { job.Company, what }.Where(part => part.Length > 0));
            rows.Add(new LinkedInRow(job.Id, job.Title.Length > 0 ? job.Title : $"Vaga {job.Id}", who, null, null, IndeedUrl(job.Host, job.Id)));
        }
        foreach (var done in now.History)
        {
            var outcome = done.Outcome;
            rows.Add(new LinkedInRow(outcome.Id, outcome.Title.Length > 0 ? outcome.Title : $"Vaga {outcome.Id}",
                $"{Format.Time(done.ProcessedAt)} · {OutcomeText(outcome)}", outcome, done.ProcessedAt, IndeedUrl(done.Host, outcome.Id)));
        }
        return rows;
    }

    private void RenderIndeedQueue()
    {
        if (Program.Indeed?.Snapshot() is not { } now) return;
        _updatingIndeed = true;
        try { SyncRows(IndeedList, _indeedRows, IndeedRows(now)); }
        finally { _updatingIndeed = false; }
        IndeedEmpty.IsVisible = _indeedRows.Count == 0;
        RenderIndeedState(now);
        ShowIndeedDetail(IndeedList.SelectedItem as LinkedInRow);
        RenderQueueSubtitle();
        UpdateIndeedTimer();
    }

    private void RenderIndeedState(IndeedQueueSnapshot now)
    {
        var state = now.State switch
        {
            IndeedState.Receiving => "Recebendo da extensão.",
            IndeedState.Stalled => "A extensão parou de dar notícia. Confira se o painel e a aba do Indeed continuam abertos.",
            IndeedState.Done => "Envio concluído.",
            IndeedState.Blocked => "Parado: o Indeed pediu uma verificação. Resolva na aba e envie de novo pela extensão.",
            IndeedState.LeftPage => "Parado: a aba saiu da página do Indeed.",
            IndeedState.Unreadable => "Parado: a extensão não conseguiu ler as páginas das vagas. O Indeed pode ter mudado o site.",
            IndeedState.Failed => "Parado por um erro. A mensagem aparece no painel da extensão.",
            _ => "Parado: não há envio em andamento.",
        };
        var text = now.Pending.Count > 0 ? $"{state}{Environment.NewLine}{now.Pending.Count} vaga(s) por ler." : state;
        if (!string.Equals(IndeedStateText.Text, text, StringComparison.Ordinal)) IndeedStateText.Text = text;
    }

    private void ShowIndeedDetail(LinkedInRow? row)
    {
        IndeedOpenButton.IsVisible = row is not null;
        IndeedViewJobButton.IsVisible = row?.Outcome?.JobId is not null;
        if (row is null) { IndeedDetailTitle.Text = "Selecione uma vaga da fila"; IndeedDetailBody.Text = ""; return; }
        IndeedDetailTitle.Text = row.Title;
        var body = $"Id no Indeed: {row.Id}{Environment.NewLine}{row.Subtitle}";
        if (!string.Equals(IndeedDetailBody.Text, body, StringComparison.Ordinal)) IndeedDetailBody.Text = body; // não desfaz um texto selecionado
    }

    /// <summary>Sem notícia não chega evento: enquanto recebe, a tela confere de tempos em tempos se a extensão sumiu.</summary>
    private void UpdateIndeedTimer()
    {
        if (!OnIndeedTab || Program.Indeed?.Snapshot().State != IndeedState.Receiving) { _indeedTimer?.Stop(); return; }
        if (_indeedTimer is null)
        {
            _indeedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _indeedTimer.Tick += (_, _) =>
            {
                if (!OnIndeedTab || Program.Indeed?.Snapshot() is not { } now) { _indeedTimer.Stop(); return; }
                if (now.State != IndeedState.Receiving) RenderIndeedQueue(); // parou: as linhas mudam junto (e o timer para)
            };
        }
        if (!_indeedTimer.IsEnabled) _indeedTimer.Start();
    }
}
