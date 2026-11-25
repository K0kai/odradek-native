using Avalonia.Controls;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Odradek.Core;
using Odradek.Core.Data;
using Odradek.LocalApi;

namespace Odradek.App;

/// <summary>Linha da lista da fila (título e subtítulo já formatados); o item completo vem junto para o detalhe.</summary>
public sealed record QueueRow(string Title, string Subtitle, QueueItem Item);

public partial class MainWindow : Window
{
    private enum Page { Overview, Jobs, Triage, Queue, Assistant, Logs, Usage, Resumes, Settings }

    /// <summary>Ícones do menu (Segoe Fluent Icons; no Windows 10 cai para Segoe MDL2 Assets).</summary>
    private static readonly Dictionary<Page, string> NavGlyphs = new()
    {
        [Page.Overview] = "\uE80F", [Page.Triage] = "\uE71C", [Page.Jobs] = "\uE8FD", [Page.Queue] = "\uE895", [Page.Assistant] = "\uE8BD", [Page.Logs] = "\uE81C",
        [Page.Usage] = "\uE9D2", [Page.Resumes] = "\uE8A5", [Page.Settings] = "\uE713",
    };

    private static readonly (Page Page, string Label)[] Pages =
    [
        (Page.Overview, "Visão geral"), (Page.Jobs, "Vagas"), (Page.Triage, "Não selecionadas"), (Page.Queue, "Filas"), (Page.Assistant, "Assistente"),
        (Page.Logs, "Logs"), (Page.Usage, "Uso e cotas"), (Page.Resumes, "Currículos"), (Page.Settings, "Configurações"),
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
    private long _jobsTotal;
    private bool _jobsLoaded;
    private DateTime _jobsUpdatedAt = DateTime.Now;
    private string _loadedKey = ""; // chave da consulta da última leitura: igual → só reconcilia a lista; diferente → troca inteira
    private NavItem? _jobsNav, _queueNav;
    // "Vagas" lista os status das vagas; "Não selecionadas" as duas abas de triagem (consulta própria). Cada página lembra a visão escolhida.
    private static readonly (string Key, string Label)[] JobViews = [.. Defaults.Statuses, (PanelQueries.AllStatuses, "Todas as vagas")];
    private static readonly (string Key, string Label)[] TriageViews = [("triage:discarded", "Descartadas pela IA"), ("triage:pending", "Aguardando triagem")];
    private static readonly (string Key, string Label)[] Views = [.. JobViews, .. TriageViews];
    private string _jobsStatus = "worth", _triageStatus = "triage:discarded";
    private bool _swappingViews;
    private string _dateFrom = "", _dateTo = ""; // lidos das caixas na thread da tela; a consulta roda em outra
    private static readonly int[] MinOptions = [0, 50, 60, 70, 80, 90];
    private readonly ObservableCollection<LogEntry> _logs = [];
    private static readonly (string Label, string Token)[] SortOptions =
        [("Mais recentes", ""), ("Maior nota", "match_desc"), ("Menor nota", "match_asc"), ("Postagem recente", "posted_desc")];
    private SettingsEditor? _settingsEditor;

    // ---------- navegação ----------

    /// <summary>Única porta de entrada das telas: mostra o painel da página, esconde os outros e dispara a leitura dela.</summary>
    private void Navigate(Page page)
    {
        _page = page;
        var onJobs = page is Page.Jobs or Page.Triage;
        JobsPane.IsVisible = onJobs; JobsControls.IsVisible = onJobs; FilterHost.IsVisible = onJobs;
        OverviewPane.IsVisible = page == Page.Overview;
        QueueTabs.IsVisible = page == Page.Queue;
        ResetConfirmations(); // uma confirmação armada não sobrevive à troca de tela
        UpdateLinkedInTimer();
        LogsPane.IsVisible = page == Page.Logs;
        UsagePane.IsVisible = page == Page.Usage;
        AssistantPane.IsVisible = page == Page.Assistant;
        SettingsTabs.IsVisible = page == Page.Settings;
        ResumesPane.IsVisible = page == Page.Resumes;
        if (!onJobs) JobsTitle.Text = Pages.First(p => p.Page == page).Label;
        JobsSubtitle.IsVisible = false;
        JobsSubtitle.ClearValue(TextBlock.ForegroundProperty);
        if (onJobs) ShowViews(page);
        FadeIn(page switch
        {
            Page.Overview => OverviewPane, Page.Jobs or Page.Triage => JobsPane, Page.Queue => QueueTabs, Page.Logs => LogsPane, Page.Usage => UsagePane, Page.Assistant => AssistantPane,
            Page.Resumes => ResumesPane, _ => SettingsTabs,
        });
        switch (page)
        {
            case Page.Overview: JobsTitle.Text = "Visão geral"; _ = LoadOverviewAsync(); break;
            case Page.Jobs or Page.Triage: UpdateJobControls(); RenderJobsHeader(); _ = ReloadJobsAsync(); break;
            case Page.Queue: ShowQueueTab(); _ = LoadQueueAsync(); break;
            case Page.Logs: _ = LoadLogsAsync(); break;
            case Page.Usage: _ = LoadUsageAsync(); break;
            case Page.Resumes: _ = LoadResumesAsync(); break;
            case Page.Settings: _ = LoadSettingsAsync(); break;
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
            var key = $"{_status}|{_pageNumber}|{_minMatch}|{_sort}|{_dateFrom}|{_dateTo}|{_query}";
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
                if (_jobsNav is not null) _jobsNav.Badge = counts.TryGetValue("worth", out var worth) ? worth : 0;
                JobsEmpty.Text = "Nenhuma vaga nesta lista."; JobsEmpty.IsVisible = page.Items.Count == 0;
                _pageNumber = page.Page; _pages = page.Pages;
                PageText.Text = $"{_pageNumber} / {_pages}";
                PrevButton.IsEnabled = _pageNumber > 1; NextButton.IsEnabled = _pageNumber < _pages;
                var selectedId = (JobsList.SelectedItem as JobRecord)?.Id;
                var sameQuery = key == _loadedKey && _jobsLoaded;
                _loadedKey = key;
                if (!sameQuery)
                {
                    // Outra consulta (aba, filtro, página): troca a lista inteira.
                    _jobs.Clear();
                    foreach (var job in page.Items) _jobs.Add(job);
                    _jobsUpdatedAt = DateTime.Now;
                }
                else if (ReconcileJobs(page.Items) is { } added)
                {
                    // Mesma consulta: só as linhas que mudaram. Quem chegou agora entra com o destaque.
                    _jobsUpdatedAt = DateTime.Now;
                    if (added.Count > 0) Dispatcher.UIThread.Post(() => FlashJobs(added), DispatcherPriority.Background);
                }
                JobsList.SelectedItem = selectedId is null ? null : _jobs.FirstOrDefault(j => j.Id == selectedId); // a seleção sobrevive às ações
                FocusRequestedJob();
                _jobsLoaded = true;
                _jobsTotal = page.Total;
                _jobsTitle = Views.First(v => v.Key == _status).Label;
                RenderJobsHeader();
                Program.Log($"Lista carregada: {page.Items.Count} de {page.Total} vagas.");
            });
        }
        catch (Exception ex) { Dispatcher.UIThread.Post(() => { if (_page is Page.Jobs or Page.Triage) JobsTitle.Text = "Não foi possível ler o banco"; }); Program.Log("Lista de vagas: " + ex.Message); }
    }

    /// <summary>Páginas "Vagas" e "Não selecionadas": troca as visões do seletor e volta à que a página tinha escolhido.</summary>
    private void ShowViews(Page page)
    {
        var views = page == Page.Triage ? TriageViews : JobViews;
        var wanted = page == Page.Triage ? _triageStatus : _jobsStatus;
        var previous = _status;
        _swappingViews = true; // trocar a lista do seletor não é uma escolha do usuário: não recarrega por conta própria
        try
        {
            StatusBox.ItemsSource = views.Select(v => v.Label).ToList();
            StatusBox.SelectedIndex = Math.Max(0, Array.FindIndex(views, v => v.Key == wanted));
        }
        finally { _swappingViews = false; }
        _status = views[StatusBox.SelectedIndex].Key;
        _jobsTitle = views[StatusBox.SelectedIndex].Label;
        if (_status != previous) _pageNumber = 1;
    }

    /// <summary>Abre a página certa (Vagas ou Não selecionadas) já na visão pedida.</summary>
    private void SelectStatus(string key)
    {
        var page = key.StartsWith("triage:", StringComparison.Ordinal) ? Page.Triage : Page.Jobs;
        if (_page != page) NavList.SelectedIndex = Array.FindIndex(Pages, p => p.Page == page);
        var views = page == Page.Triage ? TriageViews : JobViews;
        StatusBox.SelectedIndex = Array.FindIndex(views, v => v.Key == key);
    }

    /// <summary>Título da visão e "N vagas · atualizado há X min" (verde enquanto é recente).</summary>
    private void RenderJobsHeader()
    {
        if (_page is not (Page.Jobs or Page.Triage)) return;
        JobsTitle.Text = _jobsTitle;
        if (!_jobsLoaded) { JobsSubtitle.IsVisible = false; return; }
        var age = DateTime.Now - _jobsUpdatedAt;
        var recent = age.TotalSeconds < 45;
        var ago = recent ? "atualizado agora" : age.TotalMinutes < 60 ? $"atualizado há {Math.Max(1, (int)Math.Round(age.TotalMinutes))} min" : $"atualizado há {(int)age.TotalHours} h";
        JobsSubtitle.Text = $"{(_jobsTotal == 1 ? "1 vaga" : $"{_jobsTotal} vagas")} · {ago}";
        JobsSubtitle.Foreground = Palette.Brush(recent ? "Success" : "Muted");
        JobsSubtitle.IsVisible = true;
    }

    /// <summary>Dois registros da mesma vaga que a tela mostraria igual (a comparação por valor do record não serve: as listas internas são instâncias novas).</summary>
    private static bool SameJob(JobRecord a, JobRecord b) =>
        a.Id == b.Id && a.Title == b.Title && a.Company == b.Company && a.Location == b.Location && a.Status == b.Status && a.MatchScore == b.MatchScore
        && a.Notes == b.Notes && a.CoverLetter == b.CoverLetter && a.Description == b.Description
        && (a.Assessment?.Requirements.Select(r => r.Kind + r.Status).SequenceEqual(b.Assessment?.Requirements.Select(r => r.Kind + r.Status) ?? []) ?? b.Assessment is null);

    /// <summary>Acerta <see cref="_jobs"/> com a lista lida mexendo só no que mudou. Devolve null se nada mudou, senão os ids que acabaram de chegar.</summary>
    private IReadOnlyList<long>? ReconcileJobs(IReadOnlyList<JobRecord> items)
    {
        var changed = false;
        var wanted = items.Select(j => j.Id).ToHashSet();
        for (var i = _jobs.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_jobs[i].Id)) { _jobs.RemoveAt(i); changed = true; }
        var added = new List<long>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (i < _jobs.Count && _jobs[i].Id == item.Id)
            {
                if (!SameJob(_jobs[i], item)) { _jobs[i] = item; changed = true; }
                continue;
            }
            var elsewhere = -1;
            for (var k = i + 1; k < _jobs.Count; k++) if (_jobs[k].Id == item.Id) { elsewhere = k; break; }
            if (elsewhere >= 0) _jobs.RemoveAt(elsewhere); else added.Add(item.Id);
            _jobs.Insert(i, item);
            changed = true;
        }
        while (_jobs.Count > items.Count) { _jobs.RemoveAt(_jobs.Count - 1); changed = true; }
        return changed ? added : null;
    }

    /// <summary>Realça por um instante as linhas que acabaram de chegar (o fundo esmaece quando a classe sai).</summary>
    private void FlashJobs(IReadOnlyList<long> ids)
    {
        foreach (var id in ids)
        {
            var index = _jobs.ToList().FindIndex(j => j.Id == id);
            if (index < 0 || JobsList.ContainerFromIndex(index) is not ListBoxItem row) continue;
            row.Classes.Add("fresh");
            DispatcherTimer.RunOnce(() => row.Classes.Remove("fresh"), TimeSpan.FromMilliseconds(900));
        }
    }

    // ---------- filas › processamento de IA ----------

    private int _queueBadgeBusy;

    private void SetQueueBadge(long aiActive) { _aiActive = aiActive; RenderQueueBadge(); }

    /// <summary>Atualiza só o número do menu quando a fila muda com outra tela aberta (uma consulta de contagem, sem carregar a lista). Mudanças em rajada se juntam.</summary>
    private async Task RefreshQueueBadgeAsync()
    {
        // 0 = parado, 1 = consultando, 2 = consultando e chegou outra mudança no meio. Quem encontra 1 ou 2 só marca 2 e sai; quem consulta repete até sair com 1 → 0.
        if (Interlocked.Exchange(ref _queueBadgeBusy, 1) != 0) { Volatile.Write(ref _queueBadgeBusy, 2); return; }
        try
        {
            while (true)
            {
                var active = await Task.Run(() => { _ = Program.LinkedIn; return new PanelQueries(_database).QueueActiveCount(); }); // a 1ª vez lê o arquivo da fila do LinkedIn
                Dispatcher.UIThread.Post(() => SetQueueBadge(active));
                if (Interlocked.CompareExchange(ref _queueBadgeBusy, 0, 1) == 1) return; // nada novo chegou: terminou, e o estado já voltou a 0
                Volatile.Write(ref _queueBadgeBusy, 1); // chegou outra mudança: consulta de novo
            }
        }
        catch (Exception ex)
        {
            Program.Log("Selo da fila: " + ex.Message);
            Volatile.Write(ref _queueBadgeBusy, 0);
        }
    }

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
                SetQueueBadge(new[] { "pending", "running", "retry_wait" }.Sum(k => page.Counts.TryGetValue(k, out var active) ? active : 0));
                _aiSummary = summary;
                RenderQueueSubtitle();
                QueueEmpty.Text = "Nenhum item na fila."; QueueEmpty.IsVisible = page.Items.Count == 0;
                _queueRows.Clear();
                foreach (var item in page.Items)
                    _queueRows.Add(new QueueRow($"#{item.Id} · {Format.Kind(item.Kind)} · {SubjectTitle(item)}",
                        $"{Format.QueueStatus(item.Status)} · tentativas {item.Attempts}/{item.MaxAttempts}" + (item.LastStep.Length > 0 ? " · " + item.LastStep : ""), item));
                QueueList.SelectedItem = selected is null ? null : _queueRows.FirstOrDefault(r => r.Item.Id == selected);
                if (QueueList.SelectedItem is null) _ = ShowQueueDetailAsync(null);
                if (page.Items.Count == 0) QueueDetailTitle.Text = ""; // a lista já diz que está vazia
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

    // ---------- logs, uso ----------

    private async Task LoadLogsAsync()
    {
        var entries = await Task.Run(() => new EventLogStore(_database).List(200));
        _logs.Clear(); foreach (var entry in entries) _logs.Add(entry);
        LogsEmpty.Text = "Nenhum registro ainda."; LogsEmpty.IsVisible = _logs.Count == 0;
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
        else { ApifyText.Text = "Ainda sem consulta (atualiza durante a coleta)."; ApifyBar.Value = 0; }
        if (string.IsNullOrEmpty(TokenHarborText.Text)) TokenHarborText.Text = "Consultando…";
        StorageText.Text = usage.Storage is { } s
            ? $"{Format.Bytes(s.DbBytes + s.WalBytes)}" + (s.FreeBytes > 0 ? $" · {Format.Bytes(s.FreeBytes)} para compactar" : "")
              + nl + $"{Format.Number(s.Jobs)} vaga(s), {Format.Number(s.JobsWithDescription)} com descrição, {Format.Number(s.CoverLetters)} carta(s)."
              + nl + $"Pasta: {Path.GetDirectoryName(_database.Path)}"
            : "jobs.db ainda não existe.";
        BackupText.Text = usage.Backup is { } b
            ? (b.Ok ? $"Backup feito em {Format.Date(b.At, true)}: {b.Rows} linha(s) em {b.Seconds:0.##}s." : $"Com falha em {Format.Date(b.At, true)}: {b.Error}")
            : "Nenhum backup ainda.";
        var ai = usage.Ai;
        AiTitle.Text = $"Inteligência artificial — {(ai.Provider == "gemini" ? "Gemini" : ai.Provider == "openai" ? "OpenAI" : ai.Provider)} {ai.Model}";
        AiText.Text = $"Hoje ({ai.DayLabel}): {ai.Day.CallsOk} chamada(s) · {ai.Day.CallsQuotaError} erro(s) de cota · tokens {ai.Day.TokensIn} entrada / {ai.Day.TokensOut} saída{nl}"
            + $"Mês ({ai.MonthLabel}): {ai.Month.CallsOk} chamada(s) · tokens {ai.Month.TokensIn} entrada / {ai.Month.TokensOut} saída";
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

    /// <summary>Abre Configurações já no cartão da extensão (<c>--extensao</c>, a opção que o instalador oferece).</summary>
    internal async Task OpenExtensionSettingsAsync()
    {
        NavList.SelectedIndex = Array.FindIndex(Pages, p => p.Page == Page.Settings);
        await Task.Delay(600); // a troca de tela dispara a montagem das abas
        await LoadSettingsAsync();
        SettingsTabs.SelectedIndex = Array.FindIndex(SettingsLayout.Tabs, t => t.Sections.Any(s => s.Fields.Any(f => f.Key == "__extension_token_hash__")));
        await Task.Delay(400);
        _settingsEditor?.ExtensionCard?.BringIntoView();
    }

    // ---------- janela ----------

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "Versão " + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "desconhecida");
        _settingsEditor = new SettingsEditor(_database, Notify);
        GuidesButton.Click += (_, _) => GuidesWindow.Show(this, null);
        JobsList.ItemsSource = _jobs;
        // Linhas são contêineres reaproveitados: nenhum deles pode voltar à lista com a opacidade da saída esmaecida (vaga invisível, mas selecionável).
        JobsList.ContainerClearing += (_, e) => e.Container.ClearValue(Avalonia.Visual.OpacityProperty);
        JobsList.ContainerPrepared += (_, e) => e.Container.ClearValue(Avalonia.Visual.OpacityProperty);
        ShowViews(Page.Jobs); // abre em "Vagas › Vale a pena olhar"
        StatusBox.SelectionChanged += (_, _) =>
        {
            if (_swappingViews || StatusBox.SelectedIndex < 0) return;
            var views = _page == Page.Triage ? TriageViews : JobViews;
            _status = views[StatusBox.SelectedIndex].Key;
            _jobsTitle = views[StatusBox.SelectedIndex].Label;
            if (_page == Page.Triage) _triageStatus = _status; else _jobsStatus = _status;
            _pageNumber = 1;
            UpdateJobControls();
            RenderJobsHeader();
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
        WireQueueTabs();
        WireEngines();

        var navItems = Pages.Select((p, i) => new NavItem(NavGlyphs[p.Page], p.Label, $"{p.Label} (Ctrl+{(i + 1) % 10})")).ToList();
        _jobsNav = navItems[Array.FindIndex(Pages, p => p.Page == Page.Jobs)];
        _queueNav = navItems[Array.FindIndex(Pages, p => p.Page == Page.Queue)];
        NavList.ItemsSource = navItems;
        NavList.SelectedIndex = Array.FindIndex(Pages, p => p.Page == Page.Jobs);
        NavList.SelectionChanged += (_, _) => { if (NavList.SelectedIndex >= 0) Navigate(Pages[NavList.SelectedIndex].Page); };
        // "atualizado há X min" envelhece sozinho
        var ageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        ageTimer.Tick += (_, _) => RenderJobsHeader();
        ageTimer.Start();

        // Cada tela se atualiza sozinha quando o que ela mostra muda (e só então).
        ChangeBus.Shared.Published += change => Dispatcher.UIThread.Post(() =>
        {
            switch (change)
            {
                case QueueChanged: if (_page == Page.Queue) _ = LoadQueueAsync(); else _ = RefreshQueueBadgeAsync(); break;
                case LinkedInQueueChanged: RenderQueueBadge(); if (_page == Page.Queue) RenderLinkedInQueue(); break;
                case LogAppended appended: _logs.Insert(0, appended.Entry); if (_logs.Count > 500) _logs.RemoveAt(_logs.Count - 1); break;
                case LogsCleared: _logs.Clear(); break;
                case JobsChanged: _ = ReloadJobsAsync(); if (_page == Page.Overview) _ = LoadOverviewAsync(); break;
                case SettingsChanged when _page == Page.Usage: _ = LoadUsageAsync(); break;
                case QuotaChanged when _page == Page.Usage: _ = LoadUsageAsync(); break;
                case CollectorChanged: RefreshCollector(); if (_page == Page.Overview) _ = LoadOverviewAsync(); break;
                case ResumesChanged when _page == Page.Resumes: _ = LoadResumesAsync(); break;
            }
        });
        _ = ReloadJobsAsync();
        _ = RefreshQueueBadgeAsync();
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
                        QueueTabs.SelectedIndex = 0; await Task.Delay(300);
                        QueueList.SelectedIndex = _queueRows.Count > 0 ? 0 : -1; await Task.Delay(900);
                        Program.Log($"SMOKE {label} › IA: {_queueRows.Count} itens, título='{JobsTitle.Text}', detalhe {QueueDetailBody.Text?.Length ?? 0} caracteres");
                        QueueTabs.SelectedIndex = 1; await Task.Delay(500);
                        LinkedInList.SelectedIndex = _linkedInRows.Count > 0 ? 0 : -1; await Task.Delay(300);
                        Program.Log($"SMOKE {label} › LinkedIn: {_linkedInRows.Count} itens, subtítulo='{JobsSubtitle.Text}', cabeçalho='{QueueLinkedInTab.Header}', detalhe {LinkedInDetailBody.Text?.Length ?? 0} caracteres");
                        QueueTabs.SelectedIndex = 0;
                        break;
                    case Page.Overview: Program.Log($"SMOKE {label}: {OverviewHost.Children.Count} blocos"); break;
                    case Page.Triage: Program.Log($"SMOKE {label}: {_jobs.Count} vagas, título='{JobsTitle.Text}', subtítulo='{JobsSubtitle.Text}'"); break;
                    case Page.Logs: Program.Log($"SMOKE {label}: {_logs.Count} linhas"); break;
                    case Page.Usage: Program.Log($"SMOKE {label}: apify='{ApifyText.Text}' barra={ApifyBar.Value:0} ia='{AiTitle.Text}' backup={BackupText.Text?.Length}"); break;
                    case Page.Resumes: Program.Log($"SMOKE {label}: {ResumesHost.Children.Count} cartões"); break;
                    case Page.Settings: Program.Log($"SMOKE {label}: {SettingsTabs.ItemCount} abas"); break;
                    case Page.Assistant: Program.Log($"SMOKE {label}: visível={AssistantPane.IsVisible}"); break;
                }
            }
            Program.Log("SMOKE fim");
        }
        catch (Exception ex) { Program.Log("SMOKE erro: " + ex); }
        if (Avalonia.Application.Current is App app) await app.QuitAsync();
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
