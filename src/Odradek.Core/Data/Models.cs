using System.Text.Json;
using System.Text.Json.Serialization;

namespace Odradek.Core.Data;

/// <summary>Opções de JSON em snake_case, o mesmo formato que o painel React recebia (usado também nos testes de paridade).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed record AssessmentItem(string Id, string Text, string Kind, string Status, string Realism, string RealismReason,
    string Bridge, string ResumeQuote, string JobQuote, string Note, double YearsRequired = 0, int ProvenMonths = 0,
    [property: JsonIgnore] string Category = "");

public sealed record Objection(string Severity, string Reason);

/// <summary>Auditoria compacta da triagem v2 (checklist, bloqueios, itens ignorados) para o cartão da vaga.</summary>
public sealed record Assessment(string Track, bool InflatedPosting, double MustCov, double Threshold, IReadOnlyList<string> Blocks,
    IReadOnlyList<AssessmentItem> Requirements, IReadOnlyList<AssessmentItem> Responsibilities, IReadOnlyList<Objection> Objections);

public sealed record JobRecord(long Id, string Title, string Company, string Location, string Source, string Url, string? PostedAt,
    string FirstSeenAt, string Status, string Description, string Notes, string CoverLetter, int? MatchScore,
    IReadOnlyList<string> ContactEmails, Assessment? Assessment, bool HasCoverLetter);

public sealed record RunRecord(long Id, string StartedAt, string? FinishedAt, string State, long FoundCount, string Message);

public sealed record DashboardData(IReadOnlyDictionary<string, long> Counts, IReadOnlyList<JobRecord> Jobs, IReadOnlyList<RunRecord> Runs);

public sealed record JobFilters(string Status, int MinMatch, string Sort, string DateFrom, string DateTo);

public sealed record JobPage(IReadOnlyList<JobRecord> Items, long Total, int Page, int Pages, int PageSize, JobFilters Filters);

public sealed record TriageFilters(int MinMatch, string Sort, string DateFrom, string DateTo, string Q);

public sealed record TriagePage(string Tab, IReadOnlyList<JobRecord> Items, long Total, int Page, int Pages, int PageSize,
    IReadOnlyDictionary<string, long> Counts, TriageFilters Filters);

public sealed record QueueDecision(int? Score, bool Approved, string Reason, string Model, string Provider, string DecidedAt);

public sealed record QueueItem(long Id, string Kind, string Status, long Attempts, long MaxAttempts, string NextRunAt, string CreatedAt,
    string UpdatedAt, string LastStep, string Progress, bool ProgressMore, string Outcome,
    IReadOnlyDictionary<string, object?>? Subject, QueueDecision? Decision);

public sealed record QueuePage(IReadOnlyDictionary<string, long> Counts, IReadOnlyList<QueueItem> Items);

public sealed record ResumeInfo(string Language, string Filename, string Status, string Message, string Error, string Summary,
    string AnalysisJson, string? AnalyzedAt, string Provider, string Model);

public sealed record FilterMetric(long Collected, [property: JsonPropertyName("last_7d")] long Last7d,
    [property: JsonPropertyName("last_30d")] long Last30d, long Triaged, long Approved, long IgnoredByUser,
    double? ApprovalRate, bool LowPrecision);
