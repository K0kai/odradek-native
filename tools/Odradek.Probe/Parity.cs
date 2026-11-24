using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Data;

/// <summary>Roda os mesmos casos do <c>make_reference.py</c> sobre a cópia migrada pelo C# e compara com o que o Python respondeu.</summary>
public static class Parity
{
    public static int Run(string work)
    {
        var csDb = Path.Combine(work, "jobs_cs.db");
        foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(csDb + suffix)) File.Delete(csDb + suffix);
        File.Copy(Path.Combine(work, "jobs_old.db"), csDb);

        var db = new Database(csDb);
        db.Initialize();
        db.Initialize(); // idempotente: a segunda passada não pode mudar nada nem falhar
        var failures = SchemaDiff(Path.Combine(work, "jobs.db"), csDb);

        var queries = new PanelQueries(db);
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "cases.json")))!.AsArray();
        var passed = 0;
        foreach (var node in cases)
        {
            var name = (string)node!["name"]!;
            var kind = (string)node["kind"]!;
            var args = node["args"]!.AsObject();
            JsonNode? mine;
            try { mine = Execute(queries, kind, args); }
            catch (Exception ex) { Console.WriteLine($"FALHA {name}: {ex.GetType().Name}: {ex.Message}"); failures++; continue; }
            var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "ref", name + ".json")));
            var diffs = new List<string>();
            Compare("$", expected, mine, diffs);
            if (diffs.Count == 0) { passed++; continue; }
            failures++;
            Console.WriteLine($"DIVERGE {name} ({diffs.Count} diferenças)");
            foreach (var diff in diffs.Take(6)) Console.WriteLine("   " + diff);
        }
        Console.WriteLine($"{passed}/{cases.Count} casos idênticos ao Python; esquema/falhas: {failures}");

        var assessFile = Path.Combine(work, "assess_cases.json");
        if (File.Exists(assessFile))
        {
            var items = JsonNode.Parse(File.ReadAllText(assessFile))!.AsArray();
            var ok = 0;
            for (var i = 0; i < items.Count; i++)
            {
                var diffs = new List<string>();
                var mine = JsonSerializer.SerializeToNode(AssessmentReader.Compact((string)items[i]!["raw"]!), Json.Options);
                Compare("$", items[i]!["out"], mine, diffs);
                if (diffs.Count == 0) { ok++; continue; }
                failures++;
                Console.WriteLine($"DIVERGE auditoria #{i} ({diffs.Count} diferenças)");
                foreach (var diff in diffs.Take(5)) Console.WriteLine("   " + diff);
            }
            Console.WriteLine($"{ok}/{items.Count} auditorias sintéticas idênticas ao Python");
        }
        return failures == 0 ? 0 : 1;
    }

    private static int Int(JsonObject a, string key, int fallback) => a[key] is JsonNode n ? (int)n.GetValue<long>() : fallback;
    private static string Str(JsonObject a, string key, string fallback = "") => a[key] is JsonNode n ? n.GetValue<string>() : fallback;

    private static JsonNode? Execute(PanelQueries q, string kind, JsonObject a)
    {
        object? result = kind switch
        {
            "jobs" => q.Jobs(Int(a, "page", 1), Int(a, "page_size", 15), Str(a, "status", "worth"), Int(a, "min_match", 0), Str(a, "sort"), Str(a, "date_from"), Str(a, "date_to")),
            "triage" => q.Triage(Str(a, "tab", "discarded"), Int(a, "page", 1), Int(a, "page_size", 15), Int(a, "min_match", 0), Str(a, "sort"), Str(a, "date_from"), Str(a, "date_to"), Str(a, "q")),
            "queue" => q.Queue(),
            "queue_progress" => q.QueueProgress(Int(a, "id", 0)) is { } text ? new { progress = text } : null,
            "queue_assessment" => new { assessment = q.QueueAssessment(Int(a, "id", 0)) },
            "logs" => new { items = q.Logs(Int(a, "limit", 200)) },
            "resumes" => q.Resumes(),
            "filter_metrics" => new { metrics = q.FilterMetrics() },
            "dashboard" => q.Dashboard(),
            _ => throw new InvalidOperationException("caso desconhecido: " + kind),
        };
        return JsonSerializer.SerializeToNode(result, Json.Options);
    }

    private static void Compare(string path, JsonNode? expected, JsonNode? actual, List<string> diffs)
    {
        if (diffs.Count >= 40) return;
        switch (expected)
        {
            case null when actual is null: return;
            case JsonObject eo when actual is JsonObject ao:
                foreach (var (key, value) in eo)
                {
                    if (!ao.ContainsKey(key)) diffs.Add($"{path}.{key}: falta no C# (Python={Brief(value)})");
                    else Compare($"{path}.{key}", value, ao[key], diffs);
                }
                foreach (var (key, value) in ao) if (!eo.ContainsKey(key)) diffs.Add($"{path}.{key}: só no C# (C#={Brief(value)})");
                return;
            case JsonArray ea when actual is JsonArray aa:
                if (ea.Count != aa.Count) diffs.Add($"{path}: {ea.Count} itens no Python x {aa.Count} no C#");
                for (var i = 0; i < Math.Min(ea.Count, aa.Count); i++) Compare($"{path}[{i}]", ea[i], aa[i], diffs);
                return;
            case JsonValue ev when actual is JsonValue av:
                if (ev.TryGetValue<string>(out var es) && av.TryGetValue<string>(out var s)) { if (es != s) diffs.Add($"{path}: Python={Brief(ev)} C#={Brief(av)}"); }
                else if (ev.TryGetValue<double>(out var ed) && av.TryGetValue<double>(out var d)) { if (ed != d) diffs.Add($"{path}: Python={ed.ToString(System.Globalization.CultureInfo.InvariantCulture)} C#={d.ToString(System.Globalization.CultureInfo.InvariantCulture)}"); }
                else if (ev.TryGetValue<bool>(out var eb) && av.TryGetValue<bool>(out var b)) { if (eb != b) diffs.Add($"{path}: Python={eb} C#={b}"); }
                else diffs.Add($"{path}: tipos diferentes (Python={Brief(ev)} C#={Brief(av)})");
                return;
            default:
                diffs.Add($"{path}: Python={Brief(expected)} C#={Brief(actual)}");
                return;
        }
    }

    private static string Brief(JsonNode? node)
    {
        var text = node?.ToJsonString(Json.Options) ?? "null";
        return text.Length > 90 ? text[..90] + "…" : text;
    }

    /// <summary>Compara tabelas, colunas (nome/tipo/not null/padrão) e índices entre o banco migrado pelo Python e o pelo C#.</summary>
    private static int SchemaDiff(string pythonDb, string csharpDb)
    {
        static HashSet<string> Describe(string path)
        {
            using var db = Odradek.Core.Db.Open(path, readOnly: true);
            var lines = new HashSet<string>();
            foreach (var table in db.Query<string>("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"))
            {
                foreach (var c in db.Query<(string name, string type, long notnull, string? dflt, long pk)>(
                             $"SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('{table}')"))
                    lines.Add($"coluna {table}.{c.name} {c.type.ToUpperInvariant()} notnull={c.notnull} default={c.dflt} pk={c.pk}");
            }
            foreach (var name in db.Query<string>("SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%'"))
                lines.Add("índice " + name);
            foreach (var s in db.Query<string>("SELECT key FROM settings")) lines.Add("setting " + s);
            foreach (var r in db.Query<string>("SELECT key FROM form_field_rules")) lines.Add("regra " + r);
            return lines;
        }
        var python = Describe(pythonDb);
        var mine = Describe(csharpDb);
        var missing = python.Except(mine).OrderBy(x => x).ToList();
        // O Python cria as tabelas do Qpid e o índice de copilot_asks só quando precisa; o C# as cria já na abertura (IF NOT EXISTS).
        var extra = mine.Except(python).Where(l => !l.Contains("qpid_application") && !l.Contains("qpid_one_active") && !l.Contains("copilot_asks_status_idx")).OrderBy(x => x).ToList();
        foreach (var line in missing.Take(15)) Console.WriteLine("ESQUEMA falta no C#: " + line);
        foreach (var line in extra.Take(15)) Console.WriteLine("ESQUEMA só no C#:   " + line);
        Console.WriteLine($"esquema: {python.Count} itens no Python, {mine.Count} no C#, {missing.Count + extra.Count} diferenças");
        return missing.Count + extra.Count;
    }
}
