using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Odradek.Core.Data;

namespace Odradek.App;

/// <summary>
/// "Por que esta nota" como cartões: cada exigência e responsabilidade da vaga ganha uma linha com o veredito em destaque (selo colorido), a
/// explicação da IA e as duas citações que a sustentam, uma do currículo e outra da vaga. Substitui o texto corrido, que misturava tudo
/// em uma fonte pequena.
/// </summary>
internal static class AssessmentView
{
    /// <summary>Selo, fundo e texto de cada veredito (as cores acompanham o tema claro/escuro).</summary>
    private static (string Label, string Background, string Foreground) Verdict(string status) => status switch
    {
        "met" => ("Atende", "SuccessSoft", "Success"),
        "transferable" => ("Transferível", "AccentSoft", "Accent"),
        "partial" => ("Parcial", "WarningSoft", "Warning"),
        "unmet" => ("Não atende", "DangerSoft", "Danger"),
        _ => ("Sem dado", "NeutralSoft", "Muted"),
    };

    private static readonly (string Status, string One, string Many)[] SummaryOrder =
        [("met", "atende", "atendem"), ("transferable", "transferível", "transferíveis"), ("partial", "parcial", "parciais"),
         ("unmet", "não atende", "não atendem"), ("unknown", "sem dado", "sem dado")];

    // ---------- peças ----------

    private static T Themed<T>(T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }

    private static TextBlock Text(string text, double size = 13, FontWeight? weight = null, string? foreground = null, bool wrap = true)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (weight is { } w) block.FontWeight = w;
        return foreground is null ? block : Themed(block, TextBlock.ForegroundProperty, foreground);
    }

    private static Border Badge(string label, string background, string foreground, double minWidth = 0)
    {
        var text = Text(label, 11, FontWeight.SemiBold, foreground, wrap: false);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        var badge = new Border { Classes = { "badge" }, Child = text, MinWidth = minWidth, VerticalAlignment = VerticalAlignment.Top };
        return Themed(badge, Border.BackgroundProperty, background);
    }

    /// <summary>Trecho citado, com uma barra lateral e o rótulo de onde veio (currículo ou vaga).</summary>
    private static Control Quote(string label, string quote)
    {
        var body = new StackPanel { Spacing = 1 };
        body.Children.Add(Text(label.ToUpperInvariant(), 10, FontWeight.SemiBold, "Muted"));
        var quoted = Text("“" + quote.Trim() + "”", 12.5);
        quoted.FontStyle = FontStyle.Italic;
        body.Children.Add(quoted);
        var box = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2), Child = body };
        return Themed(box, Border.BorderBrushProperty, "CardBorder");
    }

    /// <summary>Faixa colorida com um rótulo em negrito à esquerda e o texto, que quebra linha, à direita.</summary>
    private static Control Callout(string label, string labelForeground, string text, string background)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        grid.Children.Add(Text(label, 12.5, FontWeight.SemiBold, labelForeground, wrap: false));
        var body = Text(text, 12.5);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return Callout(grid, background);
    }

    private static Control Callout(Control content, string background)
    {
        var box = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8), Child = content };
        return Themed(box, Border.BackgroundProperty, background);
    }

    private static Control Item(AssessmentItem item)
    {
        var inflated = item.Realism == "inflated";
        var (label, background, foreground) = inflated ? ("Desconsiderado", "NeutralSoft", "Muted") : Verdict(item.Status);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(Text(item.Text, 13.5, FontWeight.Medium));
        if (inflated)
            body.Children.Add(Text("Desconsiderado: " + (item.RealismReason.Length > 0 ? item.RealismReason : "incoerente com o próprio anúncio"), 12, foreground: "Muted"));
        else if (item.Bridge.Length > 0)
            body.Children.Add(Text(item.Bridge, 12.5, foreground: "Muted"));
        if (item.YearsRequired > 0 && !inflated)
        {
            // tempo exigido × comprovado pelas datas do currículo (a mesma conta que rebaixa o veredito quando falta)
            var enough = item.ProvenMonths / 12.0 + 0.25 >= item.YearsRequired;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            row.Children.Add(Text($"Exige {item.YearsRequired:0.#} {(item.YearsRequired == 1 ? "ano" : "anos")}", 12, FontWeight.SemiBold, wrap: false));
            row.Children.Add(Text("Comprovado: " + (item.ProvenMonths == 0 ? "nenhum tempo" : Odradek.Core.Candidate.JobAssessment.Duration(item.ProvenMonths)), 12, FontWeight.SemiBold, enough ? "Success" : "Danger", wrap: false));
            body.Children.Add(row);
        }
        if (item.Note.Length > 0 && !(item.YearsRequired > 0 && item.Note.StartsWith("exige", StringComparison.Ordinal))) // o aviso de tempo já está na linha acima
        {
            body.Children.Add(Callout("Aviso", "Warning", item.Note, "WarningSoft"));
        }
        if (item.ResumeQuote.Length > 0) body.Children.Add(Quote("Seu currículo", item.ResumeQuote));
        if (item.JobQuote.Length > 0) body.Children.Add(Quote("Na vaga", item.JobQuote));
        if (inflated) body.Opacity = 0.7;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("104,*") };
        var badge = Badge(label, background, foreground, minWidth: 92);
        badge.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(badge);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return new Border { Classes = { "notice" }, Padding = new Thickness(12, 10), Child = grid };
    }

    private static Control Section(string title, IReadOnlyList<AssessmentItem> items, string? help = null)
    {
        var section = new StackPanel { Spacing = 8 };
        var header = new StackPanel { Spacing = 2 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(Text(title, 15, FontWeight.SemiBold, wrap: false));
        titleRow.Children.Add(Text(items.Count.ToString(), 12, foreground: "Muted", wrap: false));
        header.Children.Add(Ui.WithHelp(titleRow, help));
        var counts = SummaryOrder
            .Select(o => (o.One, o.Many, N: items.Count(i => i.Realism != "inflated" && i.Status == o.Status)))
            .Where(c => c.N > 0).Select(c => $"{c.N} {(c.N == 1 ? c.One : c.Many)}").ToList();
        var ignored = items.Count(i => i.Realism == "inflated");
        if (ignored > 0) counts.Add($"{ignored} desconsiderada{(ignored == 1 ? "" : "s")}");
        if (counts.Count > 0) header.Children.Add(Text(string.Join("  ·  ", counts), 12, FontWeight.SemiBold));
        section.Children.Add(header);
        foreach (var item in items) section.Children.Add(Item(item));
        return section;
    }

    private static Control Objection(Objection o)
    {
        var (label, background, foreground) = o.Severity switch
        {
            "blocker" => ("Impeditivo", "DangerSoft", "Danger"),
            "major" => ("Relevante", "WarningSoft", "Warning"),
            _ => ("Pequeno", "NeutralSoft", "Muted"),
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("104,*") };
        var badge = Badge(label, background, foreground, minWidth: 92);
        badge.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(badge);
        var reason = Text(o.Reason, 13);
        Grid.SetColumn(reason, 1);
        grid.Children.Add(reason);
        return new Border { Classes = { "notice" }, Padding = new Thickness(12, 10), Child = grid };
    }

    // ---------- conjunto ----------

    /// <summary>Todas as seções da auditoria, na ordem em que se lê: nota, bloqueios, exigências, responsabilidades, diferenciais e dúvidas da revisão.</summary>
    public static Control Build(Assessment a)
    {
        var root = new StackPanel { Spacing = 14 };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(Text("Por que esta nota", 16, FontWeight.SemiBold, wrap: false));
        titleRow.Children.Add(Badge(a.Track == "transferable" ? "Trilha secundária" : "Trilha principal", "AccentSoft", "Accent"));
        if (a.InflatedPosting) titleRow.Children.Add(Badge("Anúncio inflado", "WarningSoft", "Warning"));
        root.Children.Add(Ui.WithHelp(titleRow, "Cada exigência da vaga é conferida com o seu currículo. O selo é o julgamento da IA; as citações mostram em que ele se apoiou."));

        var percent = Math.Round(a.MustCov * 100);
        var minimum = Math.Round(a.Threshold * 100);
        var reached = percent >= minimum;
        var coverage = new StackPanel { Spacing = 4 };
        var coverageText = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var coverageLabel = Ui.WithHelp(Text("Obrigatórios essenciais cobertos", 13, FontWeight.SemiBold, wrap: false),
            "O mínimo vem da “Tolerância da seleção”, em Configurações › Triagem.");
        coverageLabel.HorizontalAlignment = HorizontalAlignment.Left;
        coverageText.Children.Add(coverageLabel);
        var figure = Text($"{percent:0}%  ·  mínimo {minimum:0}%", 13, FontWeight.SemiBold, reached ? "Success" : "Danger", wrap: false);
        Grid.SetColumn(figure, 1);
        coverageText.Children.Add(figure);
        coverage.Children.Add(coverageText);
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = percent };
        bar.Bind(ProgressBar.ForegroundProperty, bar.GetResourceObservable(reached ? "Success" : "Danger"));
        coverage.Children.Add(bar);
        root.Children.Add(coverage);

        foreach (var block in a.Blocks)
        {
            root.Children.Add(Callout("Bloqueio", "Danger", block, "DangerSoft"));
        }

        var musts = a.Requirements.Where(i => i.Kind == "must").ToList();
        var nice = a.Requirements.Where(i => i.Kind != "must").ToList();
        if (musts.Count > 0) root.Children.Add(Section("Exigências obrigatórias", musts));
        if (a.Responsibilities.Count > 0) root.Children.Add(Section("Responsabilidades da vaga", a.Responsibilities));
        if (nice.Count > 0) root.Children.Add(Section("Diferenciais", nice, "Desejáveis: ajudam, mas não reprovam."));

        if (a.Objections.Count > 0)
        {
            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(Ui.WithHelp(Text("Pontos de atenção da revisão", 15, FontWeight.SemiBold, wrap: false),
                "Uma segunda leitura cética procura motivos para a vaga não servir."));
            foreach (var o in a.Objections) section.Children.Add(Objection(o));
            root.Children.Add(section);
        }
        return root;
    }
}
