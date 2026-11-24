using Avalonia.Controls;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Dapper;
using Odradek.Core;
using Odradek.Core.Data;
using Odradek.LocalApi;

namespace Odradek.App;

/// <summary>Linha da lista da fila (título e subtítulo já formatados); o item completo vem junto para o detalhe.</summary>
public sealed record QueueRow(string Title, string Subtitle, QueueItem Item);

public partial class MainWindow : Window
{
    private enum Page { Jobs, Queue, Assistant, Logs, Usage, Resumes, Settings, Rules }

    /// <summary>Ícones do menu (Segoe Fluent Icons; no Windows 10 cai para Segoe MDL2 Assets).</summary>
    private static readonly Dictionary<Page, string> NavGlyphs = new()
    {
        [Page.Jobs] = "\uE8FD", [Page.Queue] = "\uE895", [Page.Assistant] = "\uE8BD", [Page.Logs] = "\uE81C",
        [Page.Usage] = "\uE9D2", [Page.Resumes] = "\uE8A5", [Page.Settings] = "\uE713", [Page.Rules] = "\uE70F",
    };

    private static readonly (Page Page, string Label)[] Pages =
    [
        (Page.Jobs, "Vagas"), (Page.Queue, "Filas de IA"), (Page.Assistant, "Assistente"), (Page.Logs, "Logs"),
        (Page.Usage, "Uso e cotas"), (Page.Resumes, "Currículos"), (Page.Settings, "Configurações"), (Page.Rules, "Regras de formulário"),
    ];

    private Page _page = Page.Jobs;
    private bool _firstOpen = true;
    private readonly ObservableCollection<JobRecord> _jobs = [];
    private readonly ObservableCollection<QueueRow> _queueRows = [];
    private readonly Database _database = Program.Database; // leitura e ações: o esquema do banco já tem de estar atual (o app Python migra; este não)
    private int _reloadPending;
    private string _status = "worth";
    private string _sort = "";
    private int _pageNumber = 1, _pages = 1;
    private int _minMatch;
    private string _query = "";
    private string _jobsTitle = "Vale a pena olhar";
    // Os status das vagas mais as duas abas de "Não selecionadas" (consulta de triagem).
    private static readonly (string Key, string Label)[] Views =
        [.. Defaults.Statuses, ("triage:discarded", "Descartadas pela IA"), ("triage:pending", "Aguardando triagem"), (PanelQueries.AllStatuses, "Todas as vagas")];
    private string _dateFrom = "", _dateTo = ""; // lidos das caixas na thread da tela; a consulta roda em outra
    private static readonly int[] MinOptions = [0, 50, 60, 70, 80, 90];
    private readonly ObservableCollection<LogEntry> _logs = [];
    private static readonly (string Label, string Token)[] SortOptions =
        [("Mais recentes", ""), ("Maior nota", "match_desc"), ("Menor nota", "match_asc"), ("Postagem recente", "posted_desc")];
    private SettingsEditor? _settingsEditor;
    private RulesEditor? _rulesEditor;

    // ---------- navegação ----------

    /// <summary>Única porta de entrada das telas: mostra o painel da página, esconde os outros e dispara a leitura dela.</summary>
    private void Navigate(Page page)
    {
        _page = page;
        var onJobs = page == Page.Jobs;
        JobsPane.IsVisible = onJobs; JobsControls.IsVisible = onJobs; FilterHost.IsVisible = onJobs;
        QueuePane.IsVisible = page == Page.Queue;
        LogsPane.IsVisible = page == Page.Logs;
        UsagePane.IsVisible = page == Page.Usage;
        AssistantPane.IsVisible = page == Page.Assistant;
        SettingsTabs.IsVisible = page == Page.Settings;
        ResumesPane.IsVisible = page == Page.Resumes;
        RulesPane.IsVisible = page == Page.Rules;
        if (!onJobs) JobsTitle.Text = Pages.First(p => p.Page == page).Label;
        JobsSubtitle.IsVisible = false;
        FadeIn(page switch
        {
            Page.Jobs => JobsPane, Page.Queue => QueuePane, Page.Logs => LogsPane, Page.Usage => UsagePane, Page.Assistant => AssistantPane,
            Page.Resumes => ResumesPane, Page.Settings => SettingsTabs, _ => RulesPane,
        });
        switch (page)
        {
            case Page.Jobs: JobsTitle.Text = _jobsTitle; UpdateJobControls(); _ = ReloadJobsAsync(); break;
            case Page.Queue: _ = LoadQueueAsync(); break;
            case Page.Logs: _ = LoadLogsAsync(); break;
            case Page.Usage: _ = LoadUsageAsync(); break;
            case Page.Resumes: _ = LoadResumesAsync(); break;
            case Page.Settings: _ = LoadSettingsAsync(); break;
            case Page.Rules: _ = LoadRulesAsync(); break;
        }
    }

    // ---------- vagas ----------

    /// <summary>Botões que dependem da aba de vagas escolhida: ignorar em lote só em "Vale a pena olhar", conferir só em "Descartadas pela IA".</summary>
    private void UpdateJobControls()
    {
        BulkExpander.IsVisible = _status == "worth";
        ReviewedButton.IsVisible = _status == "triage:discarded";
        PurgeIgnoredButton.IsVisible = _status == "ignored";
        ResetConfirmations(); // uma confirmação armada não sobrevive à troca de aba
        // A busca por texto só existe nas abas de triagem (o filtro das outras listas não a usa): nas demais o campo fica apagado.
        var searchable = _status.StartsWith("triage:", StringComparison.Ordinal);
        SearchField.Opacity = searchable ? 1 : 0.45;
        SearchBox.IsEnabled = searchable;
        ToolTip.SetTip(SearchField, searchable ? null : "A busca por texto vale nas abas de Não selecionadas.");
        UpdateFilterChrome();
    }

    /// <summary>Lê a lista de vagas fora da tela e troca a lista. Várias mudanças seguidas viram uma leitura só.</summary>
    private async Task ReloadJobsAsync()
    {
        if (Interlocked.Exchange(ref _reloadPending, 1) == 1) return;
        await Task.Delay(150);
        Interlocked.Exchange(ref _reloadPending, 0);
        try
        {
            var queries = new PanelQueries(_database);
            var page = await Task.Run(() =>
            {
                if (!_status.StartsWith("triage:", StringComparison.Ordinal))
                {
                    var j = queries.Jobs(_pageNumber, 50, _status, _minMatch, _sort, _dateFrom, _dateTo);
                    return (Items: j.Items, Total: j.Total, Page: j.Page, Pages: j.Pages);
                }
                var t = queries.Triage(_status["triage:".Length..], _pageNumber, 50, _minMatch, _sort, _dateFrom, _dateTo, _query);
                return (Items: t.Items, Total: t.Total, Page: t.Page, Pages: t.Pages);
            });
            var counts = await Task.Run(() => queries.Dashboard().Counts);
            var sidebar = Defaults.Statuses.Where(st => counts.ContainsKey(st.Key)).Select(st => new CountRow(st.Label, counts[st.Key])).ToList();
            Dispatcher.UIThread.Post(() =>
            {
                CountsList.ItemsSource = sidebar;
                JobsEmpty.Text = "Nenhuma vaga nesta lista."; JobsEmpty.IsVisible = page.Items.Count == 0;
                _pageNumber = page.Page; _pages = page.Pages;
                PageText.Text = $"{_pageNumber} / {_pages}";
                PrevButton.IsEnabled = _pageNumber > 1; NextButton.IsEnabled = _pageNumber < _pages;
                var selectedId = (JobsList.SelectedItem as JobRecord)?.Id;
                _jobs.Clear();
                foreach (var job in page.Items) _jobs.Add(job);
                JobsList.SelectedItem = selectedId is null ? null : _jobs.FirstOrDefault(j => j.Id == selectedId); // a seleção sobrevive às ações
                _jobsTitle = $"{Views.First(v => v.Key == _status).Label} ({page.Total})";
                if (_page == Page.Jobs) JobsTitle.Text = _jobsTitle;
                Program.Log($"Lista carregada: {page.Items.Count} de {page.Total} vagas.");
            });
        }
        catch (Exception ex) { Dispatcher.UIThread.Post(() => { if (_page == Page.Jobs) JobsTitle.Text = "Não foi possível ler o banco"; }); Program.Log("Lista de vagas: " + ex.Message); }
    }

    // ---------- filas de IA ----------

    private async Task LoadQueueAsync()
    {
        try
        {
            var page = await Task.Run(() => new PanelQueries(_database).Queue());
            var summary = string.Join("  ·  ", new[] { "pending", "running", "retry_wait", "succeeded", "failed" }
                .Select(k => $"{Format.QueueStatus(k)}: {(page.Counts.TryGetValue(k, out var n) ? n : 0)}"));
            Dispatcher.UIThread.Post(() =>
            {
                var selected = (QueueList.SelectedItem as QueueRow)?.Item.Id;
                if (_page == Page.Queue) { JobsTitle.Text = "Filas de IA"; JobsSubtitle.Text = summary; JobsSubtitle.IsVisible = true; }
                QueueEmpty.Text = "Nenhum item na fila."; QueueEmpty.IsVisible = page.Items.Count == 0;
                _queueRows.Clear();
                foreach (var item in page.Items)
                    _queueRows.Add(new QueueRow($"#{item.Id} · {Format.Kind(item.Kind)} · {SubjectTitle(item)}",
                        $"{Format.QueueStatus(item.Status)} · tentativas {item.Attempts}/{item.MaxAttempts}" + (item.LastStep.Length > 0 ? " · " + item.LastStep : ""), item));
                QueueList.SelectedItem = selected is null ? null : _queueRows.FirstOrDefault(r => r.Item.Id == selected);
                if (QueueList.SelectedItem is null) _ = ShowQueueDetailAsync(null);
                if (page.Items.Count == 0) QueueDetailTitle.Text = "Nenhum job na fila.";
            });
        }
        catch (Exception ex) { Program.Log("Fila: " + ex.Message); }
    }

    private static string Sub(QueueItem item, string key) => item.Subject is { } s && s.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static string SubjectTitle(QueueItem item)
    {
        switch (Sub(item, "type"))
        {
            case "resume": return "Currículo " + (Sub(item, "language") is { Length: > 0 } l ? l.ToUpperInvariant() : "—");
            case "worth": return "Verificação diária" + (Sub(item, "scheduled_date") is { Length: > 0 } d ? $" de {d}" : "");
            case "job": return item.Subject!["missing"] is true ? $"Vaga #{Sub(item, "job_id")} (removida)" : (Sub(item, "title") is { Length: > 0 } t ? t : "Sem título");
            default: return "";
        }
    }

    /// <summary>Detalhe de um item: assunto, decisão da IA, andamento e (carregados à parte) histórico completo e checklist.</summary>
    private async Task ShowQueueDetailAsync(QueueRow? row)
    {
        QueueOpenButton.IsVisible = false; QueueCancelButton.IsVisible = false; QueueRetryButton.IsVisible = false;
        if (row is null) { QueueDetailTitle.Text = "Selecione um item da fila"; QueueDetailBody.Text = ""; return; }
        var item = row.Item;
        var nl = Environment.NewLine;
        QueueDetailTitle.Text = $"#{item.Id} · {Format.Kind(item.Kind)}";
        QueueOpenButton.IsVisible = Sub(item, "url").Length > 0;
        QueueCancelButton.IsVisible = item.Status is "pending" or "running" or "retry_wait";
        QueueRetryButton.IsVisible = item.Kind != "linkedin_apply" && item.Status is "running" or "retry_wait" or "failed" or "cancelled";
        var head = $"{SubjectTitle(item)}{nl}{Format.QueueStatus(item.Status)} · criado {Format.Date(item.CreatedAt, true)}"
            + (item.NextRunAt.Length > 0 ? $" · próxima {Format.Date(item.NextRunAt, true)}" : "") + $" · tentativas {item.Attempts}/{item.MaxAttempts}";
        if (Sub(item, "type") == "job" && item.Subject!["missing"] is not true)
            head += nl + string.Join(" · ", new[] { Sub(item, "company"), Sub(item, "location") }.Where(s => s.Length > 0));
        if (item.Decision is { } d)
            head += nl + nl + $"{(d.Approved ? "Aprovada" : "Descartada")}" + (d.Score is { } sc ? $" · score {sc}/100" : "")
                + (d.Model.Length > 0 ? $" · {(d.Provider.Length > 0 ? d.Provider + "/" : "")}{d.Model}" : "")
                + (d.DecidedAt.Length > 0 ? $" · decidido {Format.Date(d.DecidedAt, true)}" : "")
                + (d.Reason.Length > 0 ? nl + "Raciocínio da IA: " + d.Reason : "");
        if (item.LastStep.Length > 0) head += nl + nl + item.LastStep;
        if (item.Outcome.Length > 0) head += nl + item.Outcome;
        QueueDetailBody.Text = head + (item.Progress.Length > 0 ? nl + nl + "Histórico do processamento" + (item.ProgressMore ? " (parcial)" : "") + nl + item.Progress : "");
        try
        {
            var (progress, audit) = await Task.Run(() =>
            {
                var q = new PanelQueries(_database);
                return (item.ProgressMore ? q.QueueProgress(item.Id) : null, item.Decision is null ? null : q.QueueAssessment(item.Id));
            });
            if ((QueueList.SelectedItem as QueueRow)?.Item.Id != item.Id) return; // o usuário já escolheu outro item
            var full = head;
            if (item.Progress.Length > 0 || progress is not null) full += nl + nl + "Histórico do processamento" + nl + (progress ?? item.Progress);
            if (audit is not null) full += nl + nl + Format.Assessment(audit);
            else if (item.Decision is not null) full += nl + nl + "Sem checklist detalhado para esta decisão.";
            QueueDetailBody.Text = full;
        }
        catch (Exception ex) { Program.Log("Detalhe da fila: " + ex.Message); }
    }

    // ---------- logs, regras, uso ----------

    private async Task LoadLogsAsync()
    {
        var entries = await Task.Run(() => new EventLogStore(_database).List(200));
        _logs.Clear(); foreach (var entry in entries) _logs.Add(entry);
        LogsEmpty.Text = "Nenhum registro ainda."; LogsEmpty.IsVisible = _logs.Count == 0;
    }

    private async Task LoadRulesAsync()
    {
        if (_rulesEditor is null || _rulesEditor.Dirty) return; // não passa por cima de edição não salva
        var rules = await Task.Run(() => new PanelQueries(_database).FormRules());
        _rulesEditor.Load(rules);
    }

    private async Task LoadUsageAsync()
    {
        await RenderUsageAsync(); // o que já está salvo, na hora
        if (_usageLoading) return;
        _usageLoading = true;
        try { await RefreshQuotasAsync(); await RenderUsageAsync(); }
        finally { _usageLoading = false; }
    }

    private async Task RenderUsageAsync()
    {
        var usage = await Task.Run(() => UsageReader.Read(_database));
        var nl = Environment.NewLine;
        if (usage.Apify is { } a)
        {
            ApifyText.Text = a.Error.Length > 0 ? a.Error
                : $"US$ {a.UsedUsd:0.00} de US$ {a.Limit:0.00} · {a.Percent:0}% do limite · {(a.CycleLabel.Length > 0 ? a.CycleLabel : "ciclo não informado")}{nl}Última consulta: {Format.Date(a.FetchedAt, true)}";
            ApifyBar.Value = a.Percent;
            ApifyBar.Foreground = Palette.Brush(Palette.QuotaBrushKey(a.Percent));
        }
        else { ApifyText.Text = "Sem consulta registrada ainda. A cota é atualizada durante a coleta."; ApifyBar.Value = 0; }
        if (string.IsNullOrEmpty(TokenHarborText.Text)) TokenHarborText.Text = "Consultando…";
        BackupText.Text = usage.Backup is { } b
            ? (b.Ok ? $"Backup feito em {Format.Date(b.At, true)}: {b.Rows} linha(s) em {b.Seconds:0.##}s." : $"Com falha em {Format.Date(b.At, true)}: {b.Error}")
            : "Nenhum backup feito ainda. Ele roda uma vez por dia e ao sair do app.";
        var ai = usage.Ai;
        AiTitle.Text = $"Inteligência artificial — {(ai.Provider == "gemini" ? "Gemini" : ai.Provider == "openai" ? "OpenAI" : ai.Provider)} {ai.Model}";
        AiText.Text = $"Estimativa local das chamadas feitas por este aplicativo.{nl}"
            + $"Chamadas hoje ({ai.DayLabel}): {ai.Day.CallsOk}{nl}Erros de quota hoje: {ai.Day.CallsQuotaError}{nl}Tokens hoje (entrada / saída): {ai.Day.TokensIn} / {ai.Day.TokensOut}{nl}"
            + $"Chamadas no mês ({ai.MonthLabel}): {ai.Month.CallsOk}{nl}Tokens no mês (entrada / saída): {ai.Month.TokensIn} / {ai.Month.TokensOut}";
    }

    // ---------- configurações ----------

    private async Task LoadSettingsAsync()
    {
        if (_settingsEditor is null || _settingsEditor.Dirty) return; // rascunho não salvo: não reconstrói as abas por cima
        var stored = await Task.Run(() => new SettingsStore(_database).All());
        var values = Defaults.Settings.ToDictionary(s => s.Key, s => s.Value);
        foreach (var (key, value) in stored) values[key] = value; // o que está salvo vale mais que o padrão
        _settingsEditor.Build(SettingsTabs, values);
    }

    // ---------- janela ----------

    public MainWindow()
    {
        InitializeComponent();
        _settingsEditor = new SettingsEditor(_database, Notify);
        _rulesEditor = new RulesEditor(_database, RulesHost, RulesState, RulesSaveButton, Notify);
        JobsList.ItemsSource = _jobs;
        StatusBox.ItemsSource = Views.Select(v => v.Label).ToList();
        StatusBox.SelectedIndex = 2; // "Vale a pena olhar"
        StatusBox.SelectionChanged += (_, _) =>
        {
            if (StatusBox.SelectedIndex < 0) return;
            _status = Views[StatusBox.SelectedIndex].Key;
            _pageNumber = 1;
            UpdateJobControls();
            _ = ReloadJobsAsync();
        };
        JobsList.SelectionChanged += (_, _) => ShowDetail(JobsList.SelectedItem as JobRecord);
        SortBox.ItemsSource = SortOptions.Select(o => o.Label).ToList();
        SortBox.SelectedIndex = 0;
        SortBox.SelectionChanged += (_, _) =>
        {
            if (SortBox.SelectedIndex < 0) return;
            _sort = SortOptions[SortBox.SelectedIndex].Token;
            _pageNumber = 1;
            _ = ReloadJobsAsync();
        };
        PrevButton.Click += (_, _) => { if (_pageNumber > 1) { _pageNumber--; _ = ReloadJobsAsync(); } };
        NextButton.Click += (_, _) => { if (_pageNumber < _pages) { _pageNumber++; _ = ReloadJobsAsync(); } };
        MinBox.ItemsSource = MinOptions.Select(n => n == 0 ? "Qualquer nota" : $"Nota ≥ {n}").ToList();
        MinBox.SelectedIndex = 0;
        MinBox.SelectionChanged += (_, _) => { if (MinBox.SelectedIndex < 0) return; _minMatch = MinOptions[MinBox.SelectedIndex]; _pageNumber = 1; _ = ReloadJobsAsync(); };
        WireFilterFields();
        LogsList.ItemsSource = _logs;
        _logs.CollectionChanged += (_, _) => LogsEmpty.IsVisible = _logs.Count == 0;
        LogsEmpty.IsVisible = false; // vira "Nenhum registro ainda." depois da primeira leitura
        WirePolish();
        QueueList.ItemsSource = _queueRows;
        QueueList.SelectionChanged += (_, _) => _ = ShowQueueDetailAsync(QueueList.SelectedItem as QueueRow);
        WireActions();
        WireEngines();

        NavList.ItemsSource = Pages.Select((p, i) => new NavItem(NavGlyphs[p.Page], p.Label, $"{p.Label} (Ctrl+{i + 1})")).ToList();
        NavList.SelectedIndex = 0;
        NavList.SelectionChanged += (_, _) => { if (NavList.SelectedIndex >= 0) Navigate(Pages[NavList.SelectedIndex].Page); };

        // Cada tela se atualiza sozinha quando o que ela mostra muda (e só então).
        ChangeBus.Shared.Published += change => Dispatcher.UIThread.Post(() =>
        {
            switch (change)
            {
                case QueueChanged when _page == Page.Queue: _ = LoadQueueAsync(); break;
                case LogAppended appended: _logs.Insert(0, appended.Entry); if (_logs.Count > 500) _logs.RemoveAt(_logs.Count - 1); break;
                case LogsCleared: _logs.Clear(); break;
                case JobsChanged: _ = ReloadJobsAsync(); break;
                case SettingsChanged when _page == Page.Usage: _ = LoadUsageAsync(); break;
                case QuotaChanged when _page == Page.Usage: _ = LoadUsageAsync(); break;
                case CollectorChanged: RefreshCollector(); break;
                case ResumesChanged when _page == Page.Resumes: _ = LoadResumesAsync(); break;
            }
        });
        _ = ReloadJobsAsync();
        DataText.Text = $"Dados: {Paths.DataDir}"; ToolTip.SetTip(DataText, Paths.DataDir);
        Opened += (_, _) =>
        {
            if (!_firstOpen) return;
            _firstOpen = false;
            // Meta do plano: janela visível em menos de 1 s a frio (conta desde o início do processo).
            var ms = Program.ProcessAgeMs();
            StartupText.Text = $"Janela visível {ms:F0} ms depois do início do processo.";
            Program.Log($"Janela visível em {ms:F0} ms.");
            var smoke = Environment.GetEnvironmentVariable("ODRADEK_SMOKE");
            if (smoke == "1") _ = SmokeAsync();
            else if (smoke == "2") _ = SmokeActionsAsync();
            if (Environment.GetEnvironmentVariable("ODRADEK_SHOTS") is { Length: > 0 } shots) _ = ShotsAsync(shots);
        };
    }

    /// <summary>Teste de fumaça (ODRADEK_SMOKE=1): visita cada tela, seleciona o primeiro item e registra só contagens e tamanhos (nunca o conteúdo).</summary>
    private async Task SmokeAsync()
    {
        try
        {
            await Task.Delay(1500);
            foreach (var (page, label) in Pages)
            {
                NavList.SelectedIndex = Array.FindIndex(Pages, p => p.Page == page);
                await Task.Delay(900);
                switch (page)
                {
                    case Page.Jobs:
                        JobsList.SelectedIndex = _jobs.Count > 0 ? 0 : -1; await Task.Delay(200);
                        Program.Log($"SMOKE {label}: {_jobs.Count} vagas, título='{JobsTitle.Text}', detalhe {DetailBody.Text?.Length ?? 0} caracteres, botões={JobButtons.IsVisible}, lote={BulkExpander.IsVisible}");
                        break;
                    case Page.Queue:
                        QueueList.SelectedIndex = _queueRows.Count > 0 ? 0 : -1; await Task.Delay(900);
                        Program.Log($"SMOKE {label}: {_queueRows.Count} itens, título='{JobsTitle.Text}', detalhe {QueueDetailBody.Text?.Length ?? 0} caracteres");
                        break;
                    case Page.Logs: Program.Log($"SMOKE {label}: {_logs.Count} linhas"); break;
                    case Page.Usage: Program.Log($"SMOKE {label}: apify='{ApifyText.Text}' barra={ApifyBar.Value:0} ia='{AiTitle.Text}' backup={BackupText.Text?.Length}"); break;
                    case Page.Resumes: Program.Log($"SMOKE {label}: {ResumesHost.Children.Count} cartões"); break;
                    case Page.Rules: Program.Log($"SMOKE {label}: {RulesHost.Children.Count} regras, estado='{RulesState.Text}'"); break;
                    case Page.Settings: Program.Log($"SMOKE {label}: {SettingsTabs.ItemCount} abas"); break;
                    case Page.Assistant: Program.Log($"SMOKE {label}: visível={AssistantPane.IsVisible}"); break;
                }
            }
            Program.Log("SMOKE fim");
        }
        catch (Exception ex) { Program.Log("SMOKE erro: " + ex); }
        if (Avalonia.Application.Current is App app) await app.QuitAsync();
    }

    public void ShowServerState(string text) => ServerText.Text = text;

    public async Task LoadDbSummaryAsync()
    {
        try
        {
            var text = await Task.Run(() =>
            {
                if (!File.Exists(Paths.DbPath)) return "jobs.db ainda não existe.";
                using var db = Db.Open(readOnly: true);
                var jobs = db.ExecuteScalar<long>("SELECT COUNT(*) FROM jobs");
                var worth = db.ExecuteScalar<long>("SELECT COUNT(*) FROM jobs WHERE status='worth'");
                return $"Banco: {jobs} vagas, {worth} para revisar.";
            });
            DbText.Text = text;
        }
        catch (Exception ex)
        {
            DbText.Text = $"Banco: erro ao ler ({ex.GetType().Name}).";
            Program.Log("Falha ao ler o banco: " + ex);
        }
    }

    /// <summary>Clique no X esconde na bandeja; <see cref="App.Quit"/> fecha de verdade.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!App.Quitting)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true; // só na primeira vez da sessão: onde o app foi parar
                Toast.Show("O Odradek continua rodando na bandeja. Use Sair no menu do ícone para encerrar.");
            }
        }
        base.OnClosing(e);
    }

    private bool _trayHintShown;
}
