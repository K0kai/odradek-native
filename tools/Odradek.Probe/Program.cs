using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Odradek.Core;
using Odradek.Core.Data;
using UglyToad.PdfPig;

// Paridade da etapa 1: probe parity <pasta com jobs_old.db, jobs.db (migrado pelo Python), cases.json e ref/>
if (args.Length >= 2 && args[0] == "parity") return Parity.Run(args[1]);
// Segredos: só mostra se existe e o tamanho, nunca o valor. "roundtrip" grava uma entrada de teste e a remove no fim.
if (args.Length >= 1 && args[0] == "secrets") return SecretsProbe.Run(args.Skip(1).ToArray());

// Provas de conceito da etapa 0. Só lê (modo somente leitura) e imprime contagens, nunca o conteúdo do banco.
var candidates = new[] { Paths.DbPath, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../jobs.db")) }
    .Where(File.Exists).Distinct().ToList();
if (args.Length > 0) candidates = [args[0]];
if (candidates.Count == 0) { Console.WriteLine("Nenhum jobs.db encontrado."); return 1; }

foreach (var path in candidates)
{
    Console.WriteLine($"=== {path}");
    using var db = Db.Open(path, readOnly: true);

    var tables = db.Query<string>("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").ToList();
    Console.WriteLine($"tabelas: {tables.Count}  jobs={Count(db, "jobs")}  resumes={Count(db, "resumes")}  queue_jobs={Count(db, "queue_jobs")}  settings={Count(db, "settings")}");

    // 1) fingerprint: recalcula para cada vaga e compara com o gravado pelo Python
    var jobs = db.Query<(long id, string? url, string? company, string? title, string? location, string? fingerprint)>(
        "SELECT id, url, company, title, location, fingerprint FROM jobs").ToList();
    var bad = jobs.Where(j => Fingerprint.Of(j.url, j.company, j.title, j.location) != j.fingerprint).ToList();
    Console.WriteLine($"fingerprint: {jobs.Count - bad.Count}/{jobs.Count} iguais ao do Python");
    foreach (var j in bad.Take(5))
        Console.WriteLine($"  divergente id={j.id} urlNaoAscii={(j.url ?? "").Any(c => c > 127)} textoNaoAscii={((j.company ?? "") + (j.title ?? "") + (j.location ?? "")).Any(c => c > 127)} temQuery={(j.url ?? "").Contains('?')}");

    // 2) formato das datas gravadas pelo Python
    var iso = new Regex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\+00:00$");
    foreach (var (table, column) in new[] { ("jobs", "first_seen_at"), ("jobs", "posted_at"), ("runs", "started_at"), ("event_logs", "created_at"), ("queue_jobs", "next_run_at") })
    {
        var values = db.Query<string?>($"SELECT {column} FROM {table} WHERE {column} IS NOT NULL AND {column} <> ''").ToList();
        if (values.Count == 0) { Console.WriteLine($"datas {table}.{column}: vazio"); continue; }
        var exact = values.Count(v => iso.IsMatch(v!));
        var roundTrip = values.Count(v => Clock.TryParse(v, out var d) && (!iso.IsMatch(v!) || Clock.ToIso(d) == v));
        Console.WriteLine($"datas {table}.{column}: {exact}/{values.Count} no formato +00:00; {roundTrip}/{values.Count} leem e regravam igual");
    }
    Console.WriteLine($"agora: {Clock.NowIso()}");

    // 3) PdfPig contra o texto que o pypdf extraiu e guardou (PDF no banco ou, em bancos antigos, no disco)
    var hasFiles = tables.Contains("resume_files");
    var resumes = db.Query<(long id, string language, string? text, string? stored)>(
        "SELECT id, language, extracted_text, stored_path FROM resumes").ToList();
    foreach (var r in resumes)
    {
        byte[]? content = hasFiles ? db.ExecuteScalar<byte[]?>("SELECT content FROM resume_files WHERE resume_id = @id", new { r.id }) : null;
        if (content is null || content.Length == 0)
        {
            var file = string.IsNullOrEmpty(r.stored) ? null : Path.IsPathRooted(r.stored) ? r.stored : Path.Combine(Path.GetDirectoryName(path)!, r.stored);
            if (file is not null && File.Exists(file)) content = File.ReadAllBytes(file);
        }
        if (content is null || content.Length == 0) { Console.WriteLine($"currículo {r.language}: sem PDF (banco nem disco)"); continue; }
        using var pdf = PdfDocument.Open(content);
        var mine = Odradek.Core.Actions.ResumeActions.ExtractText(content); // extração de produção: ordem de leitura + limpeza de cabeçalhos
        var theirs = r.text ?? "";
        static HashSet<string> Words(string s) => Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(m => m.Value).ToHashSet();
        var a = Words(mine); var b = Words(theirs);
        var common = a.Intersect(b).Count();
        Console.WriteLine($"currículo {r.language}: {pdf.NumberOfPages} pág., palavras pdfpig={a.Count} pypdf={b.Count} em comum={common} ({(b.Count == 0 ? 0 : 100.0 * common / b.Count):F1}% do pypdf)");
    }
}
return 0;

static long Count(Microsoft.Data.Sqlite.SqliteConnection db, string table)
{
    try { return db.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table}"); } catch { return -1; }
}
