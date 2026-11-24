using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Odradek.App;

/// <summary>Item do menu lateral: ícone (Segoe Fluent Icons) e nome.</summary>
public sealed record NavItem(string Glyph, string Label, string Hint = "");

/// <summary>Linha do resumo de vagas na lateral.</summary>
public sealed record CountRow(string Label, long Count);

/// <summary>Cores do tema atual, buscadas pelo nome (as definições claro/escuro ficam em App.axaml).</summary>
internal static class Palette
{
    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush) return brush;
        return Brushes.Gray;
    }

    /// <summary>Selo da nota: verde a partir de 80, azul a partir de 70 (o mínimo padrão da triagem), cinza abaixo.</summary>
    public static readonly FuncValueConverter<int?, IBrush?> ScoreBackground = new(score => Brush(score switch { >= 80 => "SuccessSoft", >= 70 => "AccentSoft", _ => "NeutralSoft" }));
    public static readonly FuncValueConverter<int?, IBrush?> ScoreForeground = new(score => Brush(score switch { >= 80 => "Success", >= 70 => "Accent", _ => "Muted" }));
    /// <summary>Barra de cota: azul até 80%, âmbar de 80% a 95%, vermelha acima.</summary>
    public static string QuotaBrushKey(double percent) => percent >= 95 ? "Danger" : percent >= 80 ? "Warning" : "Accent";

    /// <summary>Nível do log: erro em vermelho, aviso em âmbar, sucesso em verde; o resto, discreto.</summary>
    public static readonly FuncValueConverter<string?, IBrush?> LevelForeground = new(level => Brush((level ?? "").ToLowerInvariant() switch
    {
        "error" => "Danger", "warning" => "Warning", "success" => "Success", _ => "Muted",
    }));

    /// <summary>"2026-11-24T03:51:10+00:00" → "24/11 00:51:10" (horário de Brasília).</summary>
    public static readonly FuncValueConverter<string?, string?> LogTime = new(Format.LogTime);

    public static readonly FuncValueConverter<int?, bool> HasScore = new(score => score is not null);
}
