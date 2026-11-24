using System.ComponentModel;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using Odradek.Core.Data;

namespace Odradek.App;

/// <summary>Item do menu lateral: ícone (Segoe Fluent Icons), nome e contador (o número de vagas para revisar aparece em "Vagas").</summary>
public sealed class NavItem(string glyph, string label, string hint = "") : INotifyPropertyChanged
{
    private long _badge;

    public string Glyph { get; } = glyph;
    public string Label { get; } = label;
    public string Hint { get; } = hint;
    public long Badge
    {
        get => _badge;
        set
        {
            if (_badge == value) return;
            _badge = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Badge)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBadge)));
        }
    }
    public bool HasBadge => _badge > 0;
    public string BadgeText => _badge > 99 ? "99+" : _badge.ToString();

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Linha do resumo de vagas na lateral.</summary>
public sealed record CountRow(string Label, long Count);

/// <summary>Pílula de skill da vaga. O tom (atende / parcial / não atende) escolhe a cor pelas classes do tema, não por um pincel fixo.</summary>
public sealed record SkillPill(string Text, string Tone, string Tip)
{
    public bool IsGood => Tone == "good";
    public bool IsPartial => Tone == "partial";
    public bool IsBad => Tone == "bad";
}

/// <summary>Cores do tema atual, buscadas pelo nome (as definições claro/escuro/Dracula ficam em App.axaml).</summary>
internal static class Palette
{
    /// <summary>Tema "Dracula": escuro, com vermelho de destaque e texto branco. Herda do escuro para os controles do Fluent.</summary>
    public static readonly ThemeVariant Dracula = new("Dracula", ThemeVariant.Dark);

    /// <summary>Verdadeiro para o escuro e para os temas que herdam dele (Dracula).</summary>
    public static bool IsDark(ThemeVariant? variant) => variant == ThemeVariant.Dark || variant?.InheritVariant == ThemeVariant.Dark;

    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush) return brush;
        return Brushes.Gray;
    }

    /// <summary>Selo da nota: verde a partir de 80, azul a partir de 70 (o mínimo padrão da triagem), cinza abaixo.</summary>
    public static readonly FuncValueConverter<int?, IBrush?> ScoreBackground = new(score => Brush(score switch { >= 80 => "SuccessSoft", >= 70 => "AccentSoft", _ => "NeutralSoft" }));
    public static readonly FuncValueConverter<int?, IBrush?> ScoreForeground = new(score => Brush(score switch { >= 80 => "Success", >= 70 => "Accent", _ => "Muted" }));

    /// <summary>Arco do anel da nota: 0–100 vira 0–360° (um pouco menos que o círculo cheio, que o arco não desenha).</summary>
    public static readonly FuncValueConverter<int?, double> ScoreSweep = new(score => Math.Clamp((score ?? 0) * 3.6, 0, 359.9));

    /// <summary>Texto do anel: a nota, ou um traço quando a vaga ainda não foi avaliada.</summary>
    public static readonly FuncValueConverter<int?, string?> ScoreText = new(score => score is { } s ? s.ToString() : "–");

    /// <summary>Barra de cota: azul até 80%, âmbar de 80% a 95%, vermelha acima.</summary>
    public static string QuotaBrushKey(double percent) => percent >= 95 ? "Danger" : percent >= 80 ? "Warning" : "Accent";

    /// <summary>Nível do log: erro em vermelho, aviso em âmbar, sucesso em verde; o resto, discreto.</summary>
    public static readonly FuncValueConverter<string?, IBrush?> LevelForeground = new(level => Brush((level ?? "").ToLowerInvariant() switch
    {
        "error" => "Danger", "warning" => "Warning", "success" => "Success", _ => "Muted",
    }));

    /// <summary>"2026-11-24T03:51:10+00:00" → "24/11 00:51:10" (horário de Brasília).</summary>
    public static readonly FuncValueConverter<string?, string?> LogTime = new(Format.LogTime);

    public static readonly FuncValueConverter<string?, string?> SourceLabel = new(Format.SourceName);

    public static readonly FuncValueConverter<int?, bool> HasScore = new(score => score is not null);

    // Botões da linha: cada um some quando a vaga já está naquele status.
    public static readonly FuncValueConverter<string?, bool> CanApply = new(status => status != "applied");
    public static readonly FuncValueConverter<string?, bool> CanSave = new(status => status != "saved");
    public static readonly FuncValueConverter<string?, bool> CanIgnore = new(status => status != "ignored");

    /// <summary>Até quatro skills da análise da IA (obrigatórias primeiro), coloridas pelo quanto o currículo atende.</summary>
    public static IReadOnlyList<SkillPill> SkillPillsOf(Assessment? assessment)
    {
        if (assessment is null) return [];
        return assessment.Requirements
            .Where(r => r.Category == "skill" && r.Text.Trim().Length > 0)
            .OrderBy(r => r.Kind == "must" ? 0 : 1)
            .Take(4)
            .Select(r =>
            {
                var tone = r.Status switch { "met" or "transferable" => "good", "partial" => "partial", "unmet" => "bad", _ => "neutral" };
                var status = r.Status switch { "met" => "Atende", "transferable" => "Atende por experiência próxima", "partial" => "Atende em parte", "unmet" => "Não atende", _ => "Sem evidência" };
                var text = r.Text.Trim();
                return new SkillPill(text.Length > 22 ? text[..21] + "…" : text, tone, $"{(r.Kind == "must" ? "Obrigatório" : "Desejável")} · {status} · {text}");
            })
            .ToList();
    }

    public static readonly FuncValueConverter<Assessment?, IReadOnlyList<SkillPill>?> SkillPills = new(SkillPillsOf);
}
