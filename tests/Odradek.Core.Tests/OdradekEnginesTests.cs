using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Data;
using Odradek.Core.Engines;
using Odradek.Core.Queue;

namespace Odradek.Core.Tests;

/// <summary>A composição: os handlers certos na fila, a reanálise dos perfis antigos e a parada limpa.</summary>
public class OdradekEnginesTests
{
    private static async Task<T> Eventually<T>(Func<T> read, Func<T, bool> done, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        T value;
        while (!done(value = read()) && DateTime.UtcNow < deadline) await Task.Delay(50);
        return value;
    }

    /// <summary>Estado da fila e do log para as mensagens de falha (este teste já falhou uma vez de forma intermitente sem deixar rastro).</summary>
    private static string Dump(TempDb t)
    {
        using var c = t.Db.Open();
        var queue = c.Query<(long Id, string Kind, string Status, long Attempts, string? Result)>("SELECT id, kind, status, attempts, result FROM queue_jobs").Select(q => q.ToString());
        var logs = c.Query<string>("SELECT level || ' ' || source || ': ' || message FROM event_logs ORDER BY id");
        return "\nfila: " + string.Join(" | ", queue) + "\nlog: " + string.Join(" | ", logs);
    }

    [Fact]
    public async Task The_running_queue_dispatches_to_the_real_handlers_and_stops_cleanly()
    {
        using var t = new TempDb();
        var closed = t.AddJob(status: "worth");
        using (var c = t.Db.Open()) c.Execute("UPDATE jobs SET url='https://empresa.test/vaga/1' WHERE id=@closed", new { closed });
        var http = new RouteHandler().On("GET", "empresa.test", "<p>This job is no longer available</p>");
        using var engines = new OdradekEngines(t.Db, _ => "", http);
        engines.Start();
        try
        {
            var queueId = new QueueActions(t.Db).Enqueue("worth_reverify", [new("scheduled_date", "2026-11-23")], "daily:2026-11-23");
            var row = await Eventually(() => t.Db.Open().QuerySingle<(string Status, string? Result)>("SELECT status, result FROM queue_jobs WHERE id=@queueId", new { queueId }), r => r.Status == "succeeded");
            Assert.True(row.Status == "succeeded", $"status {row.Status}, resultado {row.Result}" + Dump(t));
            Assert.True(row.Result == "Verificação diária: 1 checadas, 1 inativas removidas, 0 ativas, 0 inconclusivas mantidas.", $"resultado: {row.Result}" + Dump(t));
            var closedStatus = t.Scalar<string>("SELECT status FROM jobs WHERE id=@closed", new { closed });
            Assert.True(closedStatus == "ignored", $"vaga ficou {closedStatus}" + Dump(t));

            var apply = new QueueActions(t.Db).Enqueue("job_apply", [new("job_id", closed)], $"apply:{closed}");
            var attempted = await Eventually(() => t.Scalar<int>("SELECT attempts FROM queue_jobs WHERE id=@apply", new { apply }), n => n >= 1);
            Assert.True(attempted >= 1, "o handler job_apply não foi chamado" + Dump(t));
        }
        finally { await engines.StopAsync(); }
        Assert.True(engines.Collector.Snapshot().State == "stopped", "coletor: " + engines.Collector.Snapshot().State + Dump(t));
        Assert.True(t.Db.Open().Query<string>("SELECT message FROM event_logs").ToList().Contains("Fila parada."), "sem 'Fila parada.'" + Dump(t));
    }

    [Fact]
    public void Old_resume_profiles_are_queued_once_for_reanalysis()
    {
        using var t = new TempDb();
        using (var c = t.Db.Open())
        {
            c.Execute("INSERT INTO resumes(language,original_filename,stored_path,file_sha256,extracted_text,analysis_status,analysis_summary,profile_version,created_at,updated_at) VALUES('pt','a.pdf','x','s1','t','ok','resumo','','x','x')");
            c.Execute("INSERT INTO resumes(language,original_filename,stored_path,file_sha256,extracted_text,analysis_status,analysis_summary,profile_version,created_at,updated_at) VALUES('en','b.pdf','x','s2','t','ok','resumo','2','x','x')");
            c.Execute("INSERT INTO resumes(language,original_filename,stored_path,file_sha256,extracted_text,analysis_status,analysis_summary,profile_version,created_at,updated_at) VALUES('es','c.pdf','x','s3','t','pending','','','x','x')");
        }
        Assert.Equal(["pt"], OdradekEngines.EnqueueMissingResumeProfiles(t.Db));
        Assert.Equal(["pt"], OdradekEngines.EnqueueMissingResumeProfiles(t.Db)); // de novo: reaproveita o item ativo
        Assert.Equal(1, t.Scalar<int>("SELECT COUNT(*) FROM queue_jobs WHERE kind='resume_analysis'"));
        var payload = JsonNode.Parse(t.Scalar<string>("SELECT payload FROM queue_jobs"))!.AsObject();
        Assert.Equal(("pt", true), (payload["language"]!.GetValue<string>(), payload["force"]!.GetValue<bool>()));
        Assert.Contains("Perfil verificado ausente em PT; reanálise enfileirada.", t.Db.Open().Query<string>("SELECT message FROM event_logs"));
    }
}
