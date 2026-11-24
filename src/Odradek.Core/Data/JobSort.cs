namespace Odradek.Core.Data;

/// <summary>Ordenação, intervalo de datas e expressão de nota das listas de vagas. Porta de <c>jobs/job_filters.py</c>.</summary>
public static class JobSort
{
    private static readonly HashSet<string> Tokens = ["match_asc", "match_desc", "seen_asc", "seen_desc", "posted_asc", "posted_desc"];

    /// <summary>Nota da última decisão da IA (a mesma subconsulta usada em todas as listas).</summary>
    public const string MatchExpr = """
        (
                            SELECT match_score FROM ai_decisions
                            WHERE ai_decisions.job_id = jobs.id
                            ORDER BY id DESC LIMIT 1
                          )
        """;

    /// <summary>Tokens ativos, o primeiro com prioridade; uma coluna só aparece uma vez.</summary>
    public static List<string> Parse(string? sort)
    {
        var raw = (sort ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0) return [];
        if (raw is "match" or "score_desc") return ["match_desc"];
        if (!raw.Contains(',') && !raw.Contains('+') && Tokens.Contains(raw)) return [raw];
        var separator = raw.Contains(',') ? ',' : '+';
        var result = new List<string>();
        var columns = new HashSet<string>();
        foreach (var part in raw.Split(separator))
        {
            var token = part.Trim().ToLowerInvariant();
            if (token is "match" or "score_desc") token = "match_desc";
            if (!Tokens.Contains(token)) continue;
            var column = token[..token.LastIndexOf('_')];
            if (!columns.Add(column)) continue;
            result.Add(token);
        }
        return result;
    }

    public static string Normalize(string? sort) => string.Join(',', Parse(sort));

    private static string Clause(string token) => token switch
    {
        "seen_desc" => "jobs.first_seen_at DESC",
        "seen_asc" => "jobs.first_seen_at ASC",
        "match_asc" => $"COALESCE({MatchExpr}, 0) ASC",
        "match_desc" => $"COALESCE({MatchExpr}, 0) DESC",
        "posted_asc" => "(CASE WHEN COALESCE(jobs.posted_at,'')='' THEN 1 ELSE 0 END) ASC, jobs.posted_at ASC",
        "posted_desc" => "(CASE WHEN COALESCE(jobs.posted_at,'')='' THEN 1 ELSE 0 END) ASC, jobs.posted_at DESC",
        _ => "",
    };

    public static string OrderSql(string? sort)
    {
        var clauses = Parse(sort).Select(Clause).Where(c => c.Length > 0).ToList();
        return clauses.Count == 0 ? "jobs.id DESC" : string.Join(", ", clauses) + ", jobs.id DESC";
    }

    private static readonly Lazy<TimeZoneInfo> Brasilia = new(() =>
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Brasília", "Brasília"); // sem horário de verão desde 2019
    });

    /// <summary>Meia-noite de um dia de Brasília, em UTC (<c>datetime(y, m, d, tzinfo=BRASILIA)</c>); <c>null</c> se a data for inválida.</summary>
    private static DateTime? DayStartUtc(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return null;
        var parts = text.Split('-', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var y) || !int.TryParse(parts[1], out var m) || !int.TryParse(parts[2], out var d))
            return null;
        if (y is < 1 or > 9999 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return null;
        var local = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Unspecified);
        var zone = Brasilia.Value;
        try { return TimeZoneInfo.ConvertTimeToUtc(local, zone); }
        catch (ArgumentException) { return DateTime.SpecifyKind(local - zone.BaseUtcOffset, DateTimeKind.Utc); } // hora inexistente (horário de verão antigo)
    }

    /// <summary>Dias <c>YYYY-MM-DD</c> (Brasília) em limites ISO UTC <c>[lo, hi)</c>.</summary>
    public static (string? Lo, string? Hi) DateBoundsUtc(string? dateFrom, string? dateTo)
    {
        var start = DayStartUtc(dateFrom);
        string? hi = null;
        if (DayStartUtc(dateTo) is not null)
        {
            // fim = meia-noite do dia seguinte em Brasília (soma de relógio, não de 24 h)
            var parts = dateTo!.Trim().Split('-', 3).Select(int.Parse).ToArray();
            var next = new DateTime(parts[0], parts[1], parts[2]).AddDays(1);
            hi = Clock.ToIso(DayStartUtc($"{next.Year}-{next.Month}-{next.Day}")!.Value);
        }
        return (start is null ? null : Clock.ToIso(start.Value), hi);
    }
}
