using System.Diagnostics;
using System.Globalization;
using System.Text;
using Odradek.Core;
using Odradek.Core.Data;

namespace Odradek.App;

/// <summary>Textos e datas das telas, no mesmo formato do painel React (pt-BR, fuso de Brasília).</summary>
public static class Format
{
    private static readonly TimeZoneInfo Brasilia = FindBrasilia();
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static TimeZoneInfo FindBrasilia()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.Utc;
    }

    /// <summary>"24/11 00:51:10" para a lista de logs.</summary>
    public static string? LogTime(string? value) =>
        Clock.TryParse(value, out var utc) ? TimeZoneInfo.ConvertTimeFromUtc(utc, Brasilia).ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture) : value;

    /// <summary>"23 de nov. de 2026, 14:05" (como o <c>formatDate</c> do painel); sem data devolve "Sem data".</summary>
    public static string Date(string? value, bool full = false)
    {
        if (string.IsNullOrEmpty(value)) return "Sem data";
        if (!Clock.TryParse(value, out var utc)) return value;
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, Brasilia);
        return local.ToString(full ? "dd 'de' MMM 'de' yyyy HH:mm" : "dd 'de' MMM 'de' yyyy", PtBr);
    }

    public static readonly IReadOnlyDictionary<string, string> QueueKinds = new Dictionary<string, string>
    {
        ["resume_analysis"] = "Análise de currículo", ["job_apply"] = "Triagem de vaga",
        ["worth_reverify"] = "Verificação de vaga", ["linkedin_apply"] = "Candidatura por navegador",
    };

    public static readonly IReadOnlyDictionary<string, string> QueueStatuses = new Dictionary<string, string>
    {
        ["pending"] = "Pendente", ["running"] = "Executando", ["retry_wait"] = "Aguardando nova tentativa",
        ["succeeded"] = "Concluído", ["failed"] = "Falhou", ["cancelled"] = "Cancelado",
    };

    public static string Kind(string kind) => QueueKinds.TryGetValue(kind, out var label) ? label : kind;
    public static string QueueStatus(string status) => QueueStatuses.TryGetValue(status, out var label) ? label : status;

    private static string Mark(string status) => status switch
    {
        "met" => "✓", "transferable" => "⇄", "partial" => "½", "unmet" => "✗", _ => "?",
    };

    /// <summary>Checklist "Por que esta nota" em texto (porte do <c>AssessmentView</c>); as citações vão numa linha recuada.</summary>
    public static string Assessment(Assessment a)
    {
        var nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append($"Por que esta nota — trilha {(a.Track == "transferable" ? "secundária" : "principal")}");
        if (a.InflatedPosting) sb.Append(" · anúncio inflado");
        sb.Append(nl).Append($"Cobertura dos obrigatórios essenciais: {Math.Round(a.MustCov * 100):0}% (mínimo {Math.Round(a.Threshold * 100):0}%).");
        foreach (var block in a.Blocks) sb.Append(nl).Append("Bloqueio: ").Append(block);

        void Section(string title, IEnumerable<AssessmentItem> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            sb.Append(nl).Append(nl).Append(title);
            foreach (var item in list)
            {
                var inflated = item.Realism == "inflated";
                sb.Append(nl).Append("  ").Append(Mark(item.Status)).Append(' ').Append(item.Text);
                if (inflated) sb.Append(" — desconsiderado: ").Append(item.RealismReason.Length > 0 ? item.RealismReason : "incoerente com o próprio anúncio");
                else if (item.Bridge.Length > 0) sb.Append(" — ").Append(item.Bridge);
                if (item.Note.Length > 0) sb.Append(nl).Append("      Aviso: ").Append(item.Note);
                if (item.ResumeQuote.Length > 0) sb.Append(nl).Append("      Currículo: “").Append(item.ResumeQuote).Append('”');
                if (item.JobQuote.Length > 0) sb.Append(nl).Append("      Vaga: “").Append(item.JobQuote).Append('”');
            }
        }

        Section("Obrigatórios", a.Requirements.Where(i => i.Kind == "must"));
        Section("O que a pessoa vai fazer", a.Responsibilities);
        Section("Desejáveis", a.Requirements.Where(i => i.Kind != "must"));
        if (a.Objections.Count > 0)
        {
            sb.Append(nl).Append(nl).Append("Revisão cética");
            foreach (var o in a.Objections) sb.Append(nl).Append("  ! ").Append(o.Reason).Append(" (").Append(o.Severity).Append(')');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Nome curto da fonte para o cartão: "Apify:LinkedIn Jobs: Desenvolvedor Full Stack (24h, lote 1)" → "LinkedIn Jobs"; "Remote OK" fica como está.
    /// </summary>
    public static string SourceName(string? source)
    {
        var text = (source ?? "").Trim();
        if (text.StartsWith("Apify:", StringComparison.OrdinalIgnoreCase)) text = text[6..].Split(':')[0].Trim();
        return text.Length > 0 ? text : "Apify";
    }

    /// <summary>Abre só endereços http(s) no navegador padrão; qualquer outra coisa é ignorada.</summary>
    public static bool OpenInBrowser(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); return true; }
        catch (Exception) { return false; }
    }
}
