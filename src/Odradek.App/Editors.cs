using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Odradek.Core;
using Odradek.Core.Actions;
using Odradek.Core.Collector;
using Odradek.Core.Data;

namespace Odradek.App;

/// <summary>Pedaços de tela repetidos: rótulo + controle, cartão com borda, botão.</summary>
public static class Ui
{
    public static StackPanel Labeled(string label, Control control, string? help = null) =>
        Labeled(new TextBlock { Text = label, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center }, control, help);

    /// <summary>Como <see cref="Labeled(string, Control, string?)"/>, com um cabeçalho livre (rótulo + selo, por exemplo).</summary>
    public static StackPanel Labeled(Control header, Control control, string? help = null)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(WithHelp(header, help));
        panel.Children.Add(control);
        return panel;
    }

    /// <summary>O controle com o marcador (?) ao lado; sem explicação, o próprio controle.</summary>
    public static Control WithHelp(Control control, string? help)
    {
        if (string.IsNullOrEmpty(help)) return control;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(control);
        row.Children.Add(new HelpMark { Text = help });
        return row;
    }

    public static Border Card(Control child) => new() { Child = child, Classes = { "card" } };

    public static async Task<bool> CopyAsync(Control from, string value)
    {
        try
        {
            if (TopLevel.GetTopLevel(from)?.Clipboard is not { } clipboard) return false;
            await clipboard.SetTextAsync(value);
            return true;
        }
        catch (Exception ex)
        {
            Program.Log("Não consegui copiar: " + ex.Message);
            return false;
        }
    }

    /// <summary>Selo colorido (fundo e texto pelas cores do tema, que acompanham a troca claro/escuro).</summary>
    public static void Paint(Border badge, TextBlock text, string backgroundKey, string foregroundKey)
    {
        badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable(backgroundKey));
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(foregroundKey));
    }

    public static Button Button(string text, Action onClick)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Chama <paramref name="changed"/> quando o valor editável do controle muda (e não a cada passagem do mouse).</summary>
    public static void OnEdited(Control control, Action changed) =>
        control.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty || e.Property == ToggleButton.IsCheckedProperty
                || e.Property == SelectingItemsControl.SelectedIndexProperty || e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) changed();
        };
}

/// <summary>
/// Configurações editáveis, em 4 abas com rascunho: cada aba mostra "Alterações não salvas" e tem o seu botão Salvar. Só os campos
/// alterados são enviados (os segredos só quando preenchidos) e a validação é a de <see cref="SettingsActions"/>.
/// </summary>
public sealed class SettingsEditor(Database db, Action<string, bool> notify)
{
    private sealed class Entry(SettingField field, Func<string> read, Action<string> reset)
    {
        public SettingField Field { get; } = field;
        public Func<string> Read { get; } = read;
        public Action<string> Reset { get; } = reset; // define o valor "salvo" (limpa o campo de segredo)
        public string Baseline { get; set; } = read();
        public bool Dirty => Field.Kind == FieldKind.Secret ? Read().Length > 0 : Read() != Baseline;
    }

    private sealed class TabState
    {
        public required SettingTab Tab { get; init; }
        public required TabItem Item { get; init; }
        public List<Entry> Entries { get; } = [];
        public TextBlock State { get; } = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
        public Button Save { get; } = new() { Content = "Salvar", IsEnabled = false, Classes = { "primary" } };
    }

    private readonly List<TabState> _tabs = [];

    /// <summary>Alguma aba tem rascunho não salvo (a tela não reconstrói as abas nesse caso).</summary>
    public bool Dirty => _tabs.Any(t => t.Entries.Any(e => e.Dirty));

    public void Build(TabControl host, IReadOnlyDictionary<string, string> values)
    {
        var selected = Math.Max(0, host.SelectedIndex);
        _tabs.Clear();
        _secretViews.Clear();
        _providerRead = null;
        foreach (var tab in SettingsLayout.Tabs)
        {
            var state = new TabState { Tab = tab, Item = new TabItem { Header = tab.Label } };
            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 12, 12, 12), Width = 720, HorizontalAlignment = HorizontalAlignment.Left };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            top.Children.Add(state.Save);
            top.Children.Add(state.State);
            panel.Children.Add(top);
            state.Save.Click += async (_, _) => await SaveAsync(state);
            foreach (var section in tab.Sections) panel.Children.Add(BuildSection(state, section, values));
            state.Item.Content = new ScrollViewer { Content = panel };
            _tabs.Add(state);
            Refresh(state);
        }
        host.ItemsSource = _tabs.Select(t => t.Item).ToList();
        host.SelectedIndex = Math.Min(selected, _tabs.Count - 1);
    }

    private static void OpenGuide(Control from, string id) => GuidesWindow.Show(TopLevel.GetTopLevel(from) as Window, id);

    private Control BuildSection(TabState state, SettingSection section, IReadOnlyDictionary<string, string> values)
    {
        var card = new StackPanel { Spacing = 8 };
        var title = Ui.WithHelp(new TextBlock { Text = section.Title, Classes = { "h2" }, VerticalAlignment = VerticalAlignment.Center }, section.Help);
        if (section.Guide.Length == 0) card.Children.Add(title);
        else
        {
            var guide = new Button { Content = section.Guide == Guides.Actors ? "Guia dos actors" : "Guia passo a passo", Classes = { "quiet", "link" } };
            guide.Click += (_, _) => OpenGuide(guide, section.Guide == SettingsLayout.AiGuide
                ? (_providerRead?.Invoke() == "openai" ? Guides.OpenAi : Guides.Gemini) // o do provedor escolhido agora na tela
                : section.Guide);
            var header = new DockPanel();
            DockPanel.SetDock(guide, Dock.Right);
            header.Children.Add(guide);
            title.HorizontalAlignment = HorizontalAlignment.Left;
            header.Children.Add(title);
            card.Children.Add(header);
        }
        foreach (var field in section.Fields)
        {
            if (field.Key == "__extension_token_hash__") { card.Children.Add(BuildExtension()); continue; }
            var (control, entry) = BuildField(field, values);
            card.Children.Add(control);
            if (field.Kind == FieldKind.Filter) card.Children.Add(BuildFilterAction(state, field, values));
            if (entry is null) continue;
            state.Entries.Add(entry);
        }
        if (section.Title == "E-mail (SMTP)")
        {
            var result = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
            var test = Ui.Button("Enviar e-mail de teste", () => { });
            test.Click += async (_, _) =>
            {
                test.IsEnabled = false; result.Text = "Enviando…";
                try { var r = await new EmailActions(db).SendTestAsync(); result.Text = r.Notice; notify(r.Notice, !r.Ok); }
                finally { test.IsEnabled = true; }
            };
            ToolTip.SetTip(test, "Usa o que já está salvo: salve a aba antes.");
            card.Children.Add(test);
            card.Children.Add(result);
        }
        return Ui.Card(card);
    }

    private (Control Control, Entry? Entry) BuildField(SettingField field, IReadOnlyDictionary<string, string> values)
    {
        var raw = values.TryGetValue(field.Key, out var v) ? v : "";
        switch (field.Kind)
        {
            case FieldKind.Filter:
            {
                var shown = new SelectableTextBlock { Text = SettingsLayout.Display(field, values), TextWrapping = TextWrapping.Wrap };
                _filterTexts[field.Key] = shown;
                return (shown, null); // o título da seção já diz qual filtro é
            }
            case FieldKind.Toggle:
            {
                var box = new CheckBox { Content = field.Label, IsChecked = raw == "1" };
                var entry = new Entry(field, () => box.IsChecked == true ? "1" : "0", s => box.IsChecked = s == "1");
                Attach(box, entry);
                return (Ui.WithHelp(box, field.Help), entry);
            }
            case FieldKind.Select:
            {
                var options = (field.Options ?? []).ToList();
                if (options.All(o => o.Value != raw)) options.Insert(0, (raw, raw.Length == 0 ? "—" : raw));
                var combo = new ComboBox { ItemsSource = options.Select(o => o.Label).ToList(), SelectedIndex = options.FindIndex(o => o.Value == raw), MinWidth = 260 };
                var entry = new Entry(field, () => combo.SelectedIndex >= 0 ? options[combo.SelectedIndex].Value : raw, s => combo.SelectedIndex = options.FindIndex(o => o.Value == s));
                Attach(combo, entry);
                if (field.Key == "ai_provider") _providerRead = entry.Read; // a chave da IA mostra o estado do provedor escolhido
                return (Ui.Labeled(field.Label, combo), entry);
            }
            case FieldKind.Sources:
            {
                var boxes = SettingsLayout.SourceOptions.Select(o => (o.Id, Box: new CheckBox { Content = o.Label })).ToList();
                var panel = new StackPanel { Spacing = 2 };
                string Read()
                {
                    var items = raw.Split(',').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList();
                    foreach (var (id, box) in boxes)
                    {
                        if (box.IsChecked == true && !items.Contains(id)) items.Add(id);
                        if (box.IsChecked != true) items.Remove(id);
                    }
                    return string.Join(',', items);
                }
                foreach (var (id, box) in boxes) { box.IsChecked = raw.Split(',').Select(s => s.Trim().ToLowerInvariant()).Contains(id); panel.Children.Add(box); }
                var entry = new Entry(field, Read, s => { foreach (var (id, box) in boxes) box.IsChecked = s.Split(',').Contains(id); });
                foreach (var (_, box) in boxes) Attach(box, entry);
                return (Ui.Labeled(field.Label, panel), entry);
            }
            case FieldKind.Secret:
            {
                var box = new TextBox { PasswordChar = '•', MinWidth = 320 };
                var entry = new Entry(field, () => box.Text ?? "", _ => box.Text = "");
                Attach(box, entry);

                // Selo ao lado do rótulo (como o "Configurada ✓" do painel web): verde com a chave no cofre, azul com uma nova digitada
                // e ainda não salva, cinza sem chave. A chave da IA segue o provedor escolhido agora na tela, não só o salvo.
                var badgeText = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold };
                var badge = new Border { Classes = { "badge" }, Child = badgeText };
                void Update()
                {
                    var provider = _providerRead?.Invoke() ?? (values.TryGetValue("ai_provider", out var p) ? p : null);
                    var (stored, origin) = SecretStore.Lookup(SettingsLayout.SecretName(field, provider));
                    if (entry.Read().Length > 0)
                    {
                        badgeText.Text = "●  Nova chave: salve para aplicar";
                        Ui.Paint(badge, badgeText, "AccentSoft", "Accent");
                        box.PlaceholderText = "";
                    }
                    else if (stored.Length > 0)
                    {
                        badgeText.Text = origin == SecretStore.Origin.Environment ? "✓  Configurada (variável de ambiente)" : "✓  Configurada";
                        Ui.Paint(badge, badgeText, "SuccessSoft", "Success");
                        box.PlaceholderText = "Deixe vazio para manter a atual";
                    }
                    else
                    {
                        badgeText.Text = "Não configurada";
                        Ui.Paint(badge, badgeText, "NeutralSoft", "Muted");
                        box.PlaceholderText = "Cole a chave aqui";
                    }
                }
                _secretViews[entry] = Update;
                Update();
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                header.Children.Add(new TextBlock { Text = field.Label, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
                header.Children.Add(badge);
                ToolTip.SetTip(badge, "O valor fica no Cofre de Credenciais do Windows e nunca volta para a tela.");
                return (Ui.Labeled(header, box, field.Help), entry);
            }
            default:
                if (SettingsLayout.Sliders.TryGetValue(field.Key, out var range))
                {
                    var current = int.TryParse(raw, out var n) ? Math.Clamp(n, range.Min, range.Max) : range.Default;
                    var slider = new Slider { Minimum = range.Min, Maximum = range.Max, Value = current, TickFrequency = 1, IsSnapToTickEnabled = true, MinWidth = 320 };
                    var caption = new TextBlock { Opacity = 0.7 };
                    void Show() => caption.Text = field.Key == "job_selection_optimism"
                        ? $"{(int)slider.Value} — exige {SettingsLayout.Coverage((int)slider.Value)}% dos requisitos obrigatórios"
                        : (int)slider.Value <= 1 ? "até 24 horas" : $"até {(int)slider.Value} dias";
                    Show();
                    slider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Show(); };
                    var entry = new Entry(field, () => ((int)slider.Value).ToString(), s => slider.Value = int.TryParse(s, out var m) ? m : range.Default) { Baseline = current.ToString() };
                    Attach(slider, entry);
                    var panel = new StackPanel { Spacing = 2 };
                    panel.Children.Add(slider); panel.Children.Add(caption);
                    return (Ui.Labeled(field.Label, panel), entry);
                }
                if (SettingsLayout.PromptDefaults.TryGetValue(field.Key, out var promptDefault))
                {
                    // Vazio = padrão: a caixa mostra o texto padrão (editável). Texto igual ao padrão não é guardado, então o app segue o padrão quando ele melhora.
                    string Shown(string s) => s.Trim().Length == 0 ? promptDefault : s;
                    var prompt = new TextBox { Text = Shown(raw), MinWidth = 320, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 220, MaxHeight = 420 };
                    var promptEntry = new Entry(field, () => prompt.Text ?? "", s => prompt.Text = Shown(s));
                    Attach(prompt, promptEntry);
                    var status = new TextBlock { Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                    var restore = new Button { Content = "Restaurar o padrão", Classes = { "quiet" } };
                    void Refresh()
                    {
                        var isDefault = (prompt.Text ?? "").Trim() == promptDefault.Trim();
                        status.Text = isDefault ? "Texto padrão" : "Personalizado";
                        restore.IsVisible = !isDefault;
                    }
                    prompt.TextChanged += (_, _) => Refresh();
                    restore.Click += (_, _) => prompt.Text = promptDefault;
                    Refresh();
                    var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                    bar.Children.Add(status); bar.Children.Add(restore);
                    var box = new StackPanel { Spacing = 4 };
                    box.Children.Add(prompt); box.Children.Add(bar);
                    return (Ui.Labeled(field.Label, box, field.Help), promptEntry);
                }
                var text = new TextBox { Text = raw, MinWidth = 320, TextWrapping = TextWrapping.Wrap };
                if (SettingsLayout.MultiLine.Contains(field.Key)) { text.AcceptsReturn = true; text.MinHeight = 110; text.MaxHeight = 260; }
                var textEntry = new Entry(field, () => text.Text ?? "", s => text.Text = s);
                Attach(text, textEntry);
                return (Ui.Labeled(field.Label, text, field.Help), textEntry);
        }
    }

    private readonly Dictionary<Entry, Action> _refreshers = [];
    private readonly Dictionary<Entry, Action> _secretViews = []; // atualiza o selo de cada chave (depois de digitar, de trocar o provedor e de salvar)
    private Func<string>? _providerRead;
    private readonly Dictionary<string, SelectableTextBlock> _filterTexts = [];
    private readonly Dictionary<string, Button> _filterButtons = [];
    private readonly HashSet<string> _generating = []; // filtros sendo gerados agora; sobrevive à reconstrução das abas (sair e voltar da tela)

    /// <summary>
    /// "Gerar com IA": salva as edições não salvas da aba (menos segredos) e substitui só o filtro deste campo. A geração roda fora da tela:
    /// sair de Configurações não a interrompe, e ao voltar o botão novo já aparece "Gerando…" e recebe o resultado quando ela termina.
    /// </summary>
    private Control BuildFilterAction(TabState state, SettingField field, IReadOnlyDictionary<string, string> values)
    {
        var slot = field.Key == LinkedInFilters.MainKey ? "main" : "secondary";
        var label = values.TryGetValue(field.Key, out var current) && current.Trim().Length > 0 ? "Gerar de novo com IA" : "Gerar com IA";
        var button = Ui.Button(label, () => { });
        button.HorizontalAlignment = HorizontalAlignment.Left;
        _filterButtons[field.Key] = button;
        if (_generating.Contains(field.Key)) { button.IsEnabled = false; button.Content = "Gerando…"; }
        button.Click += async (_, _) =>
        {
            if (!_generating.Add(field.Key)) return; // já tem uma geração deste filtro em andamento
            var changed = state.Entries.Where(e => e.Dirty && e.Field.Kind != FieldKind.Secret).ToList();
            var form = changed.ToDictionary(e => e.Field.Key, e => e.Read());
            var idleLabel = label;
            button.IsEnabled = false; button.Content = "Gerando…";
            var done = false;
            try
            {
                var filter = await Task.Run(() => Program.Engines.Filters.GenerateAndSaveAsync(form, slot));
                foreach (var entry in changed) entry.Baseline = entry.Read();
                // As abas podem ter sido reconstruídas enquanto a IA trabalhava: o resultado vai para o texto que está na tela agora.
                if (_filterTexts.TryGetValue(field.Key, out var shown))
                    shown.Text = SettingsLayout.Display(field, new Dictionary<string, string> { [field.Key] = PyJson.Serialize(filter) });
                done = true;
                notify($"Filtro {(slot == "main" ? "principal" : "secundário")} gerado e salvo.", false);
            }
            catch (ActionException ex) { notify(ex.Message, true); }
            catch (Exception ex) { notify($"Não foi possível gerar o filtro: {ex.Message}", true); }
            finally
            {
                _generating.Remove(field.Key);
                if (_filterButtons.TryGetValue(field.Key, out var visible)) { visible.IsEnabled = true; visible.Content = done ? "Gerar de novo com IA" : idleLabel; }
                Refresh(state);
            }
        };
        return button;
    }

    private void Attach(Control control, Entry entry) => Ui.OnEdited(control, () => { if (_refreshers.TryGetValue(entry, out var refresh)) refresh(); });

    private void Refresh(TabState state)
    {
        foreach (var entry in state.Entries)
        {
            _refreshers[entry] = () => Refresh(state);
            if (_secretViews.TryGetValue(entry, out var updateBadge)) updateBadge();
        }
        var dirty = state.Entries.Any(e => e.Dirty);
        state.State.Text = dirty ? "Alterações não salvas" : "";
        state.Save.IsEnabled = dirty;
        state.Item.Header = state.Tab.Label + (dirty ? " •" : "");
    }

    private async Task SaveAsync(TabState state)
    {
        var changed = state.Entries.Where(e => e.Dirty).ToList();
        if (changed.Count == 0) return;
        var data = changed.ToDictionary(e => e.Field.Key, e => e.Read());
        state.Save.IsEnabled = false;
        try
        {
            var notice = await Task.Run(() => new SettingsActions(db).Save(data));
            foreach (var entry in changed)
            {
                if (entry.Field.Kind == FieldKind.Secret) { entry.Reset(""); continue; }
                entry.Baseline = entry.Read();
            }
            notify(notice, false);
        }
        catch (ActionException ex) { notify(ex.Message, true); }
        catch (Exception ex) { notify($"Não foi possível salvar: {ex.Message}", true); }
        Refresh(state);
    }

    private Control BuildExtension()
    {
        var actions = new ExtensionTokenActions(db);
        var panel = new StackPanel { Spacing = 6 };
        var status = new TextBlock { Opacity = 0.7 };
        var token = new SelectableTextBlock { FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        void ShowStatus()
        {
            var (configured, created) = actions.Status();
            status.Text = configured ? $"Token configurado (criado em {Format.Date(created, true)})." : "Nenhum token gerado.";
        }
        ShowStatus();
        var generate = Ui.Button("Gerar novo token", () => { });
        generate.Click += async (_, _) =>
        {
            var value = await Task.Run(actions.Issue);
            token.Text = value; token.IsVisible = true; ShowStatus();
            notify("Token gerado. Copie agora: ele não será exibido de novo.", false);
        };
        var revoke = Ui.Button("Revogar", () => { });
        revoke.Click += async (_, _) => { await Task.Run(actions.Revoke); token.IsVisible = false; ShowStatus(); notify("Token revogado.", false); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(generate); buttons.Children.Add(revoke);
        panel.Children.Add(ExtensionInstallPanel.Build(notify));
        // O caminho de antes, para quando o "Conectar ao Odradek" da extensão não servir.
        var manual = new StackPanel { Spacing = 6 };
        manual.Children.Add(Ui.Labeled("URL do painel (“Backend URL” na extensão)",new SelectableTextBlock { Text = "http://127.0.0.1:8765" }));
        manual.Children.Add(status);
        manual.Children.Add(buttons);
        manual.Children.Add(token);
        panel.Children.Add(new Expander { Header = ManualPairingHeader, Content = manual, HorizontalAlignment = HorizontalAlignment.Stretch });

        // O Qpid (assistente do desktop) usa as rotas /api/qpid/* com um token próprio, guardado no Cofre do Windows: aqui só se copia para colar lá.
        var qpid = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
        qpid.Children.Add(Ui.WithHelp(new TextBlock { Text = "Token do Qpid", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            "No Qpid, abra Integrações › Odradek, cole o token e salve. O Qpid se conecta em http://127.0.0.1:8765 com o Odradek aberto."));
        var copyQpid = Ui.Button("Copiar token do Qpid", () => { });
        copyQpid.HorizontalAlignment = HorizontalAlignment.Left;
        copyQpid.Click += async (_, _) =>
        {
            try
            {
                var value = await Task.Run(() => Program.QpidApiToken);
                if (TopLevel.GetTopLevel(copyQpid)?.Clipboard is not { } clipboard) { notify("Não foi possível acessar a área de transferência.", true); return; }
                await clipboard.SetTextAsync(value);
                notify("Token do Qpid copiado. Cole em Integrações › Odradek no Qpid.", false);
            }
            catch (Exception ex) { notify($"Não foi possível obter o token do Qpid: {ex.Message}", true); }
        };
        qpid.Children.Add(copyQpid);
        panel.Children.Add(qpid);
        ExtensionCard = panel;
        return panel;
    }

    public const string ManualPairingHeader = "Conectar manualmente (token)";

    /// <summary>O cartão da extensão da última montagem das abas, para a tela rolar até ele.</summary>
    public Control? ExtensionCard { get; private set; }
}
