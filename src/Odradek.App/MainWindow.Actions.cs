using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Odradek.Core;
using Odradek.Core.Actions;
using Odradek.Core.Data;

namespace Odradek.App;

/// <summary>Fase 3: tudo o que grava. Cada ação roda fora da tela, mostra o aviso no topo e deixa a lista se atualizar pelo <see cref="ChangeBus"/>.</summary>
public partial class MainWindow
{
    private DispatcherTimer? _noticeTimer;
    private JobRecord? _detailJob;
    private string _detailSignature = ""; // o que os cartões e a carta mostram: se não muda, não são recriados (e o texto selecionado fica)
    private bool _confirmingEmail;
    private int _bulkPreview = -1;
    private BulkIgnoreCriteria _bulkCriteria = BulkIgnoreCriteria.Empty;
    private readonly Dictionary<string, IStorageFile> _resumeFiles = [];
    private readonly Dictionary<string, bool> _resumeForce = [];

    // ---------- aviso e execução ----------

    private void Notify(string message, bool error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            NoticeText.Text = message;
            NoticeText.Foreground = error ? Brushes.IndianRed : Brushes.SeaGreen;
            NoticeText.IsVisible = true;
            _noticeTimer?.Stop();
            _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 12 : 7) };
            _noticeTimer.Tick += (_, _) => { NoticeText.IsVisible = false; _noticeTimer?.Stop(); };
            _noticeTimer.Start();
        });
        Program.Log((error ? "AVISO: " : "") + message);
    }

    /// <summary>Roda a ação fora da tela; erro de validação vira aviso vermelho, qualquer outra falha também (com o motivo).</summary>
    private async Task RunAsync(Func<string> work, Action? after = null)
    {
        try { var notice = await Task.Run(work); Notify(notice, false); after?.Invoke(); }
        catch (ActionException ex) { Notify(ex.Message, true); }
        catch (Exception ex) { Notify($"Não foi possível concluir: {ex.Message}", true); }
    }

    private static TopLevel? TopOf(Control control) => TopLevel.GetTopLevel(control);

    // ---------- vagas ----------

    private void ShowDetail(JobRecord? job)
    {
        var sameJob = job is not null && _detailJob?.Id == job.Id;
        _detailJob = job;
        JobButtons.IsVisible = job is not null;
        DetailStatus.IsVisible = job is not null;
        EmailPanel.IsVisible = false; _confirmingEmail = false;
        OpenJobButton.IsVisible = job is not null && job.Url.Length > 0;
        if (job is null)
        {
            DetailTitle.Text = "Selecione uma vaga"; DetailMeta.Text = ""; DetailStatus.IsVisible = false; DetailBody.Text = ""; NotesBox.Text = "";
            DetailSections.Children.Clear(); DetailLetter.Children.Clear(); DescriptionHeading.IsVisible = false; _detailSignature = "";
            NotesBox.IsEnabled = NotesSaveButton.IsEnabled = false;
            return;
        }
        NotesBox.IsEnabled = NotesSaveButton.IsEnabled = true;
        AppliedButton.IsVisible = job.Status != "applied";
        SavedButton.IsVisible = job.Status != "saved";
        IgnoredButton.IsVisible = job.Status != "ignored";
        WorthButton.IsVisible = job.Status != "worth";
        LetterTxtButton.IsVisible = LetterPdfButton.IsVisible = job.HasCoverLetter;
        var nl = Environment.NewLine;
        DetailTitle.Text = job.Title;
        var statusLabel = Defaults.Statuses.FirstOrDefault(s => s.Key == job.Status).Label ?? job.Status;
        DetailStatusText.Text = statusLabel;
        var (statusBg, statusFg) = job.Status switch
        {
            "applied" => ("SuccessSoft", "Success"), "saved" => ("AccentSoft", "Accent"), "worth" => ("WarningSoft", "Warning"), _ => ("NeutralSoft", "Muted"),
        };
        DetailStatus.Background = Palette.Brush(statusBg); DetailStatusText.Foreground = Palette.Brush(statusFg);
        DetailMeta.Text = $"{job.Company} · {job.Location} · {Format.SourceName(job.Source)} · coletada {Format.Date(job.FirstSeenAt)}"
            + (job.MatchScore is { } m ? $" · nota {m}" : "")
            + (job.ContactEmails.Count > 0 ? nl + "Contato: " + string.Join(", ", job.ContactEmails) : "");
        // A auditoria da nota vira cartões (veredito em selo, explicação e citações); a descrição e a carta ficam sob títulos próprios.
        DescriptionHeading.IsVisible = job.Description.Trim().Length > 0;
        DetailBody.Text = job.Description;
        var signature = $"{job.Id}\n{JsonSerializer.Serialize(job.Assessment)}\n{job.HasCoverLetter}\n{job.CoverLetter}";
        if (signature != _detailSignature)
        {
            _detailSignature = signature;
            DetailSections.Children.Clear();
            if (job.Assessment is { } a) DetailSections.Children.Add(AssessmentView.Build(a));
            DetailLetter.Children.Clear();
            if (job.HasCoverLetter)
            {
                DetailLetter.Children.Add(new TextBlock { Text = "Carta de apresentação", Classes = { "h2" }, FontSize = 16 });
                DetailLetter.Children.Add(new SelectableTextBlock { Text = job.CoverLetter, TextWrapping = Avalonia.Media.TextWrapping.Wrap, LineHeight = 21 });
            }
        }
        if (!sameJob) NotesBox.Text = job.Notes; // não apaga o que está sendo digitado quando a lista recarrega por outro motivo

        if (job.Status != "applied" && job.HasCoverLetter && job.ContactEmails.Count > 0)
        {
            EmailPanel.IsVisible = true;
            EmailList.Children.Clear();
            foreach (var email in job.ContactEmails) EmailList.Children.Add(new CheckBox { Content = email, IsChecked = true, Tag = email });
            EmailSendButton.Content = "Enviar por e-mail…"; EmailCancelButton.IsVisible = false;
        }
    }

    /// <summary>
    /// Muda o status e atualiza a tela na hora: a linha sai da lista (ou troca de status), o total e o contador do menu acompanham, e a gravação
    /// segue em segundo plano. Se falhar, avisa e relê a lista, que devolve a linha.
    /// </summary>
    private async Task SetJobStatusAsync(JobRecord job, string status)
    {
        var index = _jobs.ToList().FindIndex(j => j.Id == job.Id);
        if (index >= 0)
        {
            var inTriage = _status.StartsWith("triage:", StringComparison.Ordinal);
            var leaves = inTriage || (_status != PanelQueries.AllStatuses && status != _status);
            if (leaves)
            {
                var fading = JobsList.ContainerFromIndex(index);
                if (fading is not null) fading.Opacity = 0; // sai com um esmaecer curto
                _jobsTotal = Math.Max(0, _jobsTotal - 1);
                var id = job.Id;
                DispatcherTimer.RunOnce(() =>
                {
                    var at = _jobs.ToList().FindIndex(j => j.Id == id);
                    if (at >= 0) _jobs.RemoveAt(at);
                    JobsEmpty.IsVisible = _jobs.Count == 0;
                    // O contêiner esmaecido volta para a lista de reaproveitamento (ou, se a lista foi recarregada nesse meio tempo, já mostra outra vaga):
                    // sem devolver a opacidade, a vaga seguinte que o usar fica invisível, embora dê para selecioná-la.
                    fading?.ClearValue(Avalonia.Visual.OpacityProperty);
                }, TimeSpan.FromMilliseconds(150));
            }
            else
            {
                var selected = ReferenceEquals(JobsList.SelectedItem, _jobs[index]);
                _jobs[index] = job with { Status = status };
                if (selected) JobsList.SelectedIndex = index;
            }
        }
        if (_jobsNav is not null && job.Status != status)
            _jobsNav.Badge = Math.Max(0, _jobsNav.Badge + (status == "worth" ? 1 : job.Status == "worth" ? -1 : 0));
        _jobsUpdatedAt = DateTime.Now;
        RenderJobsHeader();
        try { Notify(await Task.Run(() => new JobActions(_database).SetStatus(job.Id, status)), false); }
        catch (ActionException ex) { Notify(ex.Message, true); _ = ReloadJobsAsync(); }
        catch (Exception ex) { Notify($"Não foi possível concluir: {ex.Message}", true); _ = ReloadJobsAsync(); }
    }

    private IReadOnlyList<string> PickedEmails() => EmailList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();

    private async Task SaveLetterAsync(string format)
    {
        if (_detailJob is not { } job) return;
        var body = await Task.Run(() => LetterExport.LoadBody(_database, job.Id));
        if (body is null) { Notify("Carta não encontrada.", true); return; }
        var top = TopOf(this);
        if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salvar carta", SuggestedFileName = $"cover-letter-{job.Id}.{format}", DefaultExtension = format,
            FileTypeChoices = [new FilePickerFileType(format == "pdf" ? "PDF" : "Texto") { Patterns = [$"*.{format}"] }],
        });
        if (file is null) return;
        var data = format == "pdf" ? LetterExport.Pdf(body) : LetterExport.Txt(body);
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(data);
        Notify($"Carta salva em {file.Name}.", false);
    }

    private void UpdateBulkState()
    {
        _bulkPreview = -1;
        BulkPreviewText.Text = ""; BulkApplyButton.IsVisible = false;
        var all = BulkAll.IsChecked == true;
        foreach (var box in new[] { BulkCountry, BulkFoundFrom, BulkFoundTo, BulkPostedFrom, BulkPostedTo, BulkMatchMin, BulkMatchMax }) box.IsEnabled = !all;
    }

    private BulkIgnoreCriteria ReadBulk() => new("", BulkCountry.Text ?? "", BulkFoundFrom.Text ?? "", BulkFoundTo.Text ?? "", BulkPostedFrom.Text ?? "",
        BulkPostedTo.Text ?? "", BulkMatchMin.Text ?? "", BulkMatchMax.Text ?? "", BulkAll.IsChecked == true);

    // Ações destrutivas em dois cliques (ação → confirmar, com Cancelar). Só uma fica armada por vez.
    private readonly Dictionary<Button, (Button Cancel, string Idle)> _confirmables = [];
    private Button? _armed;

    private void ResetConfirmations()
    {
        foreach (var (button, (cancel, idle)) in _confirmables) { button.Content = idle; cancel.IsVisible = false; }
        _armed = null;
    }

    /// <summary>
    /// Liga um botão destrutivo ao padrão "clicar, confirmar". Com <paramref name="count"/>, o 1º clique conta o que será afetado
    /// (fora da tela), mostra o número no botão e não arma se não houver nada a fazer. <paramref name="ask"/> e <paramref name="nothing"/> trocam
    /// os textos quando a ação não é uma limpeza.
    /// </summary>
    private void WireConfirm(Button button, Button cancel, Func<string> work, Func<int>? count = null, Func<int, string>? ask = null,
        string nothing = "Nada para limpar aqui.", Action? after = null)
    {
        var idle = button.Content?.ToString() ?? "";
        _confirmables[button] = (cancel, idle);
        button.Click += async (_, _) =>
        {
            if (_armed != button)
            {
                ResetConfirmations();
                var n = -1;
                if (count is not null)
                {
                    try { n = await Task.Run(count); }
                    catch (Exception ex) { Notify($"Não foi possível contar: {ex.Message}", true); return; }
                    if (n == 0) { Notify(nothing, false); return; }
                }
                _armed = button; button.Content = ask?.Invoke(n) ?? (n > 0 ? $"Confirmar limpeza de {n}" : "Confirmar limpeza"); cancel.IsVisible = true;
                return;
            }
            ResetConfirmations();
            _ = RunAsync(work, after);
        };
        cancel.Click += (_, _) => ResetConfirmations();
    }

    private void WireActions()
    {
        // vaga: status e anotação
        void SetStatus(string status) { if (_detailJob is { } job) _ = SetJobStatusAsync(job, status); }
        // Botões da linha (Candidatar, Salvar, Ignorar): um só tratador para a lista; o Tag do botão traz o status.
        JobsList.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is not Button { Tag: string status, DataContext: JobRecord job }) return;
            e.Handled = true;
            if (status == "applied") Format.OpenInBrowser(job.Url); // Candidatar abre a vaga e marca como aplicada
            _ = SetJobStatusAsync(job, status);
        });
        AppliedButton.Click += (_, _) => SetStatus("applied");
        SavedButton.Click += (_, _) => SetStatus("saved");
        IgnoredButton.Click += (_, _) => SetStatus("ignored");
        WorthButton.Click += (_, _) => SetStatus("worth");
        OpenJobButton.Click += (_, _) => Format.OpenInBrowser(_detailJob?.Url);
        NotesSaveButton.Click += (_, _) => { if (_detailJob is { } job) { var text = NotesBox.Text ?? ""; _ = RunAsync(() => new JobActions(_database).SetNotes(job.Id, text)); } };
        LetterTxtButton.Click += async (_, _) => await SaveLetterAsync("txt");
        LetterPdfButton.Click += async (_, _) => await SaveLetterAsync("pdf");

        // e-mail de candidatura: dois cliques (enviar → confirmar)
        EmailSendButton.Click += async (_, _) =>
        {
            if (_detailJob is not { } job) return;
            var picked = PickedEmails();
            if (picked.Count == 0) { Notify("Escolha ao menos um e-mail de contato.", true); return; }
            if (!_confirmingEmail)
            {
                _confirmingEmail = true; EmailSendButton.Content = $"Confirmar envio para {string.Join(", ", picked)}"; EmailCancelButton.IsVisible = true;
                return;
            }
            EmailSendButton.IsEnabled = false; EmailSendButton.Content = "Enviando…";
            try { Notify(await new EmailActions(_database).SendApplicationAsync(job.Id, picked), false); }
            catch (ActionException ex) { Notify(ex.Message, true); }
            catch (Exception ex) { Notify($"Falha ao enviar o e-mail: {ex.Message}", true); }
            finally { EmailSendButton.IsEnabled = true; _confirmingEmail = false; EmailSendButton.Content = "Enviar por e-mail…"; EmailCancelButton.IsVisible = false; }
        };
        EmailCancelButton.Click += (_, _) => { _confirmingEmail = false; EmailSendButton.Content = "Enviar por e-mail…"; EmailCancelButton.IsVisible = false; };

        // conferida (Não selecionadas › Descartadas)
        ReviewedButton.Click += (_, _) =>
        {
            var ids = _jobs.Select(j => j.Id).ToList();
            if (ids.Count > 0) _ = RunAsync(() => new JobActions(_database).MarkReviewed(ids));
        };

        // ignorar em lote
        foreach (var box in new[] { BulkCountry, BulkFoundFrom, BulkFoundTo, BulkPostedFrom, BulkPostedTo, BulkMatchMin, BulkMatchMax }) Ui.OnEdited(box, UpdateBulkState);
        Ui.OnEdited(BulkAll, UpdateBulkState);
        BulkPreviewButton.Click += async (_, _) =>
        {
            try
            {
                var criteria = ReadBulk();
                var count = await Task.Run(() => new JobActions(_database).BulkIgnorePreview(criteria));
                _bulkCriteria = criteria; _bulkPreview = count;
                BulkPreviewText.Text = $"{count} vaga{(count == 1 ? "" : "s")} corresponde{(count == 1 ? "" : "m")} aos critérios.";
                BulkApplyButton.IsVisible = count > 0;
                BulkApplyButton.Content = $"Confirmar e ignorar {count} vaga{(count == 1 ? "" : "s")}";
            }
            catch (ActionException ex) { Notify(ex.Message, true); _bulkPreview = -1; BulkApplyButton.IsVisible = false; }
            catch (Exception ex) { Notify($"Não foi possível contar: {ex.Message}", true); }
        };
        BulkApplyButton.Click += (_, _) =>
        {
            if (_bulkPreview < 1) return;
            var criteria = _bulkCriteria; var expected = _bulkPreview;
            _ = RunAsync(() => new JobActions(_database).BulkIgnoreApply(criteria, expected), () =>
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var box in new[] { BulkCountry, BulkFoundFrom, BulkFoundTo, BulkPostedFrom, BulkPostedTo, BulkMatchMin, BulkMatchMax }) box.Text = "";
                    BulkAll.IsChecked = false; UpdateBulkState();
                }));
        };

        // fila
        QueueEvaluateButton.Click += (_, _) => { ResetConfirmations(); _ = RunAsync(() => new QueueActions(_database).EvaluateNew()); };
        QueueRetryAllButton.Click += (_, _) => { ResetConfirmations(); _ = RunAsync(() => $"{new QueueActions(_database).RetryAll()} item(ns) reenfileirado(s)."); };
        WireConfirm(QueueClearButton, QueueClearCancel, () => $"{new QueueActions(_database).ClearTerminal()} item(ns) removido(s).");
        QueueOpenButton.Click += (_, _) => Format.OpenInBrowser((QueueList.SelectedItem as QueueRow) is { } r ? Sub(r.Item, "url") : null);
        QueueCancelButton.Click += (_, _) => QueueItemAction(id => new QueueActions(_database).Cancel(id));
        QueueRetryButton.Click += (_, _) => QueueItemAction(id => new QueueActions(_database).RetryNow(id));

        // banco local (Uso e cotas): a compactação é a mesma que a limpeza de ignoradas faz no fim
        WireConfirm(CompactButton, CompactCancel, () => new JobActions(_database).Compact(),
            () => UsageReader.Storage(_database) is { FreeBytes: > 0 } ? 1 : 0, ask: _ => "Confirmar compactação", nothing: "Não há espaço livre para recuperar.",
            after: () => _ = RenderUsageAsync());

        WireCopy();

        // logs
        LogsRefreshButton.Click += (_, _) => _ = LoadLogsAsync();
        WireConfirm(LogsClearButton, LogsClearCancel, () => $"{new EventLogStore(_database).Clear()} log(s) removido(s).");

        // vagas ignoradas: esvazia descrição, carta e auditoria (a vaga fica, para a coleta não trazê-la de volta)
        WireConfirm(PurgeIgnoredButton, PurgeIgnoredCancel, () => new JobActions(_database).PurgeIgnored(), () => new JobActions(_database).PurgeIgnoredPreview());
        ToolTip.SetTip(PurgeIgnoredButton, "Apaga descrição, carta e análise das ignoradas. A vaga, a nota e as anotações ficam.");
    }

    private void QueueItemAction(Func<long, bool> action)
    {
        if (QueueList.SelectedItem is not QueueRow row) return;
        var id = row.Item.Id;
        Task.Run(() => action(id)).ContinueWith(t => Notify(t.IsCompletedSuccessfully && t.Result ? "Fila atualizada." : "Este item não pode ser alterado agora.",!(t.IsCompletedSuccessfully && t.Result)));
    }

    // ---------- currículos ----------

    private async Task LoadResumesAsync()
    {
        var resumes = await Task.Run(() => new PanelQueries(_database).Resumes());
        ResumesHost.Children.Clear();
        foreach (var language in new[] { "pt", "en" })
        {
            resumes.TryGetValue(language, out var record);
            var card = new StackPanel { Spacing = 6 };
            card.Children.Add(Ui.WithHelp(new TextBlock { Text = $"Currículo {language.ToUpperInvariant()}  ·  {record?.Status ?? "Não enviado"}", Classes = { "h2" }, VerticalAlignment = VerticalAlignment.Center },
                "Depois do envio, a análise pela IA roda na fila (Filas › Processamento de IA); até terminar, o currículo fica pendente."));
            var nl = Environment.NewLine;
            card.Children.Add(new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = record is null ? "Envie um PDF para analisar e usar o currículo na triagem."
                    : $"{(record.Filename.Length > 0 ? record.Filename : "currículo.pdf")}{(record.Message.Length > 0 ? nl + record.Message : "")}{nl}Última análise: {Format.Date(record.AnalyzedAt, true)}"
                      + (record.Provider.Length > 0 ? $"{nl}{record.Provider} · {record.Model}" : "") + (record.Error.Length > 0 ? $"{nl}Erro: {record.Error}" : "")
                      ,
            });
            if (record is { Summary.Length: > 0 })
                card.Children.Add(new Expander
                {
                    Header = "Ver a análise do currículo", HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new SelectableTextBlock { Text = record.Summary, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Avalonia.Thickness(0, 4) },
                });
            var chosen = new TextBlock { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, Text = _resumeFiles.TryGetValue(language, out var f) ? f.Name : "Nenhum arquivo escolhido" };
            var send = new Button { Content = "Enviar currículo", IsEnabled = _resumeFiles.ContainsKey(language), Classes = { "primary" } };
            var pick = new Button { Content = "Selecionar PDF…" };
            var force = new CheckBox { Content = "Forçar nova análise", IsChecked = _resumeForce.GetValueOrDefault(language) };
            force.IsCheckedChanged += (_, _) => _resumeForce[language] = force.IsChecked == true;
            pick.Click += async (_, _) =>
            {
                var top = TopOf(this);
                if (top is null) return;
                var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = $"Currículo {language.ToUpperInvariant()} (PDF)", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
                });
                if (files.Count == 0) return;
                _resumeFiles[language] = files[0]; chosen.Text = files[0].Name; send.IsEnabled = true;
            };
            send.Click += async (_, _) =>
            {
                if (!_resumeFiles.TryGetValue(language, out var file)) return;
                send.IsEnabled = false;
                try
                {
                    byte[] bytes;
                    await using (var stream = await file.OpenReadAsync())
                    {
                        if (stream.CanSeek && stream.Length > ResumeActions.MaxPdfBytes) throw new ActionException("PDF maior que 12 MB.");
                        using var memory = new MemoryStream();
                        await stream.CopyToAsync(memory);
                        bytes = memory.ToArray();
                    }
                    var useForce = force.IsChecked == true;
                    var notice = await Task.Run(() => new ResumeActions(_database).Upload(language, file.Name, bytes, useForce));
                    _resumeFiles.Remove(language);
                    Notify(notice, false);
                }
                catch (ActionException ex) { Notify(ex.Message, true); send.IsEnabled = true; }
                catch (Exception ex) { Notify($"Falha no upload do currículo: {ex.Message}", true); send.IsEnabled = true; }
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(pick); buttons.Children.Add(chosen);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(send);
            actions.Children.Add(Ui.WithHelp(force, "Refaz a análise no envio, mesmo que ela já tenha sido feita para este arquivo."));
            if (record is not null)
            {
                var reanalyze = new Button { Content = "Reanalisar com IA" };
                reanalyze.Click += (_, _) => _ = RunAsync(() => new ResumeActions(_database).Reanalyze(language));
                actions.Children.Add(reanalyze);
            }
            card.Children.Add(buttons); card.Children.Add(actions);
            ResumesHost.Children.Add(Ui.Card(card));
        }
    }
}

/// <summary>Teste de fumaça das ações (ODRADEK_SMOKE=2): aciona os botões de verdade sobre um banco descartável e registra o que mudou (só contagens e estados).</summary>
public partial class MainWindow
{
    private static void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static T Q<T>(string sql, object? args = null)
    {
        using var db = Db.Open(readOnly: true);
        return Dapper.SqlMapper.ExecuteScalar<T>(db, sql, args)!;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<Control>()) yield return child;
    }

    private async Task Step(string name, Func<Task> body, Func<string> check)
    {
        try { await body(); await Task.Delay(700); Program.Log($"SMOKE2 {name}: {check()}"); }
        catch (Exception ex) { Program.Log($"SMOKE2 {name}: ERRO {ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task SmokeActionsAsync()
    {
        await Task.Delay(1500);
        void Go(Page page) => NavList.SelectedIndex = Array.FindIndex(Pages, p => p.Page == page);
        long jobId = 0;

        await Step("status", async () =>
        {
            Go(Page.Jobs); await Task.Delay(900);
            JobsList.SelectedIndex = 0; await Task.Delay(300);
            jobId = _detailJob?.Id ?? 0;
            Click(SavedButton);
        }, () => $"vaga {(jobId > 0 ? "escolhida" : "NENHUMA")}, status no banco={Q<string>("SELECT status FROM jobs WHERE id=@jobId", new { jobId })}, aviso='{NoticeText.Text}'");

        long rowJob = 0; long totalBefore = 0, badgeBefore = 0;
        await Step("linha-ignorar", async () =>
        {
            SelectStatus("worth"); await Task.Delay(1200);
            totalBefore = _jobsTotal; badgeBefore = _jobsNav?.Badge ?? 0;
            var button = Descendants(JobsList).OfType<Button>().FirstOrDefault(b => b.IsVisible && b.Tag as string == "ignored");
            rowJob = (button?.DataContext as JobRecord)?.Id ?? 0;
            if (button is not null) Click(button);
            await Task.Delay(400); // a linha sai e o total/contador caem antes da gravação voltar
            Program.Log($"SMOKE2 linha-ignorar: logo após o clique → lista={_jobs.Count}, total={_jobsTotal} (antes {totalBefore}), contador={_jobsNav?.Badge} (antes {badgeBefore}), linha ainda na lista={_jobs.Any(j => j.Id == rowJob)}");
        }, () => $"vaga {(rowJob > 0 ? "escolhida" : "NENHUMA")}, status no banco={Q<string>("SELECT status FROM jobs WHERE id=@rowJob", new { rowJob })}, contador={_jobsNav?.Badge}, aviso='{NoticeText.Text}'");

        long noteJob = 0;
        await Step("notas", async () =>
        {
            JobsList.SelectedIndex = 0; await Task.Delay(300);
            noteJob = _detailJob?.Id ?? 0;
            NotesBox.Text = "anotação do teste de fumaça ç 😀"; Click(NotesSaveButton);
        }, () => $"vaga {(noteJob > 0 ? "escolhida" : "NENHUMA")}, notas gravadas={Q<string>("SELECT notes FROM jobs WHERE id=@noteJob", new { noteJob }) == "anotação do teste de fumaça ç 😀"}, aviso='{NoticeText.Text}'");

        string F() => $"lista={JobsList.ItemCount}, título='{JobsTitle.Text}', limpar visível={ClearFiltersButton.IsVisible}, busca ativa={SearchBox.IsEnabled}, página={PageText.Text}";
        await Step("filtro-nota", async () => { SelectStatus("ignored"); await Task.Delay(900); MinBox.SelectedIndex = 3; }, () => "nota ≥ 70 → " + F());
        await Step("filtro-data", async () => { DateFromBox.SelectedDate = new DateTime(2026, 10, 1); }, () => $"de 01/10/2026 (_dateFrom={_dateFrom}) → " + F());
        await Step("filtro-data-futura", async () => { DateFromBox.SelectedDate = new DateTime(2030, 1, 1); }, () => "de 2030 → " + F());
        await Step("filtro-limpar", async () => { Click(ClearFiltersButton); }, () => $"limpo (nota={MinBox.SelectedIndex}, de={DateFromBox.SelectedDate}) → " + F());
        await Step("filtro-busca", async () => { SelectStatus("triage:discarded"); await Task.Delay(900); SearchBox.Text = "zzzz-nada"; await Task.Delay(600); }, () => "busca 'zzzz-nada' → " + F());
        await Step("filtro-busca-limpar", async () => { Click(SearchClearButton); }, () => $"busca limpa (texto='{SearchBox.Text}') → " + F());
        SelectStatus("worth"); await Task.Delay(500);

        await Step("lote", async () =>
        {
            SelectStatus("worth"); await Task.Delay(500);
            BulkAll.IsChecked = true; await Task.Delay(100);
            Click(BulkPreviewButton); await Task.Delay(500);
        }, () => $"prévia='{BulkPreviewText.Text}', botão confirmar visível={BulkApplyButton.IsVisible} ('{BulkApplyButton.Content}')");

        await Step("lote-aplicar", async () => { Click(BulkApplyButton); }, () => $"worth restantes={Q<long>("SELECT COUNT(*) FROM jobs WHERE status='worth'")}, aviso='{NoticeText.Text}'");

        await Step("lote-invalido", async () =>
        {
            BulkAll.IsChecked = false; BulkMatchMin.Text = "999"; Click(BulkPreviewButton); await Task.Delay(300);
        }, () => $"aviso='{NoticeText.Text}'");

        await Step("fila", async () =>
        {
            Go(Page.Queue); await Task.Delay(900);
            Click(QueueRetryAllButton);
        }, () => $"aviso='{NoticeText.Text}', pendentes={Q<long>("SELECT COUNT(*) FROM queue_jobs WHERE status='pending'")}, falhos={Q<long>("SELECT COUNT(*) FROM queue_jobs WHERE status='failed'")}");

        long queueId = 0;
        await Step("fila-cancelar", async () =>
        {
            queueId = await Task.Run(() => new QueueActions(_database).Enqueue("job_apply", [new("job_id", 999999L)])); // item novo, no topo da lista
            await Task.Delay(1200);
            QueueList.SelectedIndex = _queueRows.ToList().FindIndex(r => r.Item.Id == queueId); await Task.Delay(900);
            Program.Log($"SMOKE2 fila-cancelar: botões antes → cancelar={QueueCancelButton.IsVisible} reenfileirar={QueueRetryButton.IsVisible}");
            Click(QueueCancelButton);
        }, () => $"status no banco={Q<string>("SELECT status FROM queue_jobs WHERE id=@queueId", new { queueId })}, aviso='{NoticeText.Text}'");

        await Step("fila-reenfileirar", async () =>
        {
            await Task.Delay(900);
            QueueList.SelectedIndex = _queueRows.ToList().FindIndex(r => r.Item.Id == queueId); await Task.Delay(900);
            Program.Log($"SMOKE2 fila-reenfileirar: botões antes → cancelar={QueueCancelButton.IsVisible} reenfileirar={QueueRetryButton.IsVisible}");
            Click(QueueRetryButton);
        }, () => $"status no banco={Q<string>("SELECT status FROM queue_jobs WHERE id=@queueId", new { queueId })}, aviso='{NoticeText.Text}'");

        await Step("fila-limpar", async () =>
        {
            Click(QueueClearButton); await Task.Delay(200);
            var asked = QueueClearButton.Content?.ToString();
            Program.Log($"SMOKE2 fila-limpar 1º clique: botão='{asked}'");
            Click(QueueClearButton);
        }, () => $"aviso='{NoticeText.Text}', terminados no banco={Q<long>("SELECT COUNT(*) FROM queue_jobs WHERE status IN ('succeeded','failed','cancelled')")}");

        await Step("logs-limpar", async () =>
        {
            Go(Page.Logs); await Task.Delay(900);
            Click(LogsClearButton); await Task.Delay(200); Click(LogsClearButton);
        }, () => $"linhas na tela={_logs.Count}, no banco={Q<long>("SELECT COUNT(*) FROM event_logs")} (logs novos da própria ação contam)");

        await Step("configuracoes", async () =>
        {
            Go(Page.Settings); await Task.Delay(1200);
            SettingsTabs.SelectedIndex = 2; await Task.Delay(300); // Perfil
            var box = Descendants(SettingsTabs).OfType<TextBox>().FirstOrDefault(b => b.IsVisible && !b.AcceptsReturn && b.PasswordChar == '\0');
            if (box is not null) box.Text = "Nome do Teste";
            await Task.Delay(300);
            var save = Descendants(SettingsTabs).OfType<Button>().FirstOrDefault(b => b.IsVisible && b.Content?.ToString() == "Salvar" && b.IsEnabled);
            Program.Log($"SMOKE2 configuracoes: campo achado={box is not null}, botão Salvar habilitado={save is not null}");
            if (save is not null) Click(save);
        }, () => $"candidate_name no banco='{Q<string>("SELECT COALESCE((SELECT value FROM settings WHERE key='candidate_name'),'')")}', aviso='{NoticeText.Text}'");

        await Step("config-invalida", async () =>
        {
            SettingsTabs.SelectedIndex = 3; await Task.Delay(300);
            var port = Descendants(SettingsTabs).OfType<TextBox>().FirstOrDefault(b => b.IsVisible && b.Text == "587");
            if (port is not null) port.Text = "99999";
            await Task.Delay(300);
            var save = Descendants(SettingsTabs).OfType<Button>().FirstOrDefault(b => b.IsVisible && b.Content?.ToString() == "Salvar" && b.IsEnabled);
            if (save is not null) Click(save);
        }, () => $"aviso='{NoticeText.Text}', smtp_port no banco={Q<string>("SELECT COALESCE((SELECT value FROM settings WHERE key='smtp_port'),'(padrão)')")}");

        await Step("token", async () =>
        {
            var manual = Descendants(SettingsTabs).OfType<Expander>().FirstOrDefault(e => e.Header?.ToString() == SettingsEditor.ManualPairingHeader);
            if (manual is not null) { manual.IsExpanded = true; await Task.Delay(500); }
            var gen = Descendants(SettingsTabs).OfType<Button>().FirstOrDefault(b => b.IsVisible && b.Content?.ToString() == "Gerar novo token");
            if (gen is not null) Click(gen);
        }, () => $"hash gravado={Q<long>("SELECT COUNT(*) FROM settings WHERE key='__extension_token_hash__' AND value<>''")}, aviso='{NoticeText.Text}'");

        await Step("assistente", async () =>
        {
            Go(Page.Assistant); await Task.Delay(500);
            AssistantInput.Text = "Quantos anos de experiência você tem?"; Click(AssistantSendButton); await Task.Delay(1500);
        }, () => $"bolhas={AssistantHost.Children.Count}, estado='{AssistantState.Text}', aviso='{NoticeText.Text}', botão habilitado={AssistantSendButton.IsEnabled}");

        await Step("uso-cotas", async () => { Go(Page.Usage); await Task.Delay(2500); },
            () => $"apify='{ApifyText.Text}', tokenharbor='{TokenHarborText.Text}'");

        await Step("gerar-filtro", async () =>
        {
            Go(Page.Settings); await Task.Delay(1200);
            SettingsTabs.SelectedIndex = 0; await Task.Delay(300);
            var gen = Descendants(SettingsTabs).OfType<Button>().FirstOrDefault(b => b.IsVisible && b.Content?.ToString() is "Gerar com IA" or "Gerar de novo com IA");
            Program.Log($"SMOKE2 gerar-filtro: botão achado={gen is not null}");
            if (gen is not null) { Click(gen); await Task.Delay(1500); }
        }, () => $"aviso='{NoticeText.Text}'");

        await Step("coleta", async () =>
        {
            Go(Page.Jobs); await Task.Delay(300);
            Program.Log($"SMOKE2 coleta: antes='{CollectorText.Text}' botão='{CollectorButton.Content}'");
            Click(CollectorButton); await Task.Delay(2500);
            Program.Log($"SMOKE2 coleta: depois='{CollectorText.Text}' botão='{CollectorButton.Content}'");
            if (Program.Engines.Collector.Snapshot().State != "stopped") { Click(CollectorButton); await Task.Delay(2500); }
        }, () => $"texto='{CollectorText.Text}', botão='{CollectorButton.Content}', runs={Q<long>("SELECT COUNT(*) FROM runs")}");

        await Step("backup-e-bandeja", async () =>
        {
            var result = await Program.Engines.Backup.RunAsync();
            Program.Log($"SMOKE2 backup: configurado={Program.Engines.Backup.Configured}, resultado={(result is null ? "sem URL" : result.Ok ? "ok" : result.Error)}");
            Close(); await Task.Delay(1200); // o X esconde na bandeja e mostra o aviso da primeira vez
            Program.Log($"SMOKE2 bandeja: janela visível após o X={IsVisible}");
            Show(); await Task.Delay(300);
        }, () => $"visível={IsVisible}");

        Program.Log("SMOKE2 fim");
        if (Avalonia.Application.Current is App app) await app.QuitAsync();
    }
}
