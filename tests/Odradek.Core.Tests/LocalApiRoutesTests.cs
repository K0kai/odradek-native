using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Api;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

public class LocalApiAuthTests
{
    [Fact]
    public async Task Qpid_routes_need_the_bearer_token_and_everything_else_is_not_found()
    {
        using var api = ApiHarness.Start();
        var (status, json) = await api.Json("GET", "/api/qpid/status", token: null);
        Assert.Equal((HttpStatusCode.Unauthorized, "Token inválido."), (status, json!["error"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("GET", "/api/qpid/status", token: "outro")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("GET", "/api/qpid/status", token: ApiHarness.QpidToken + "x")).Status);
        Assert.Equal(HttpStatusCode.OK, (await api.Json("GET", "/api/qpid/status")).Status);

        var (missing, body) = await api.Json("GET", "/api/qpid/nao-existe");
        Assert.Equal((HttpStatusCode.NotFound, "Not Found"), (missing, body!["detail"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("GET", "/api/qpid/nao-existe", token: null)).Status); // a autenticação vem antes de achar a rota
        var (panel, _) = await api.Json("GET", "/api/dashboard", token: null);
        Assert.Equal(HttpStatusCode.NotFound, panel); // o painel web deixou de existir
    }

    [Fact]
    public async Task Without_a_configured_token_the_qpid_routes_answer_503()
    {
        using var api = ApiHarness.Start(qpidToken: "");
        var (status, json) = await api.Json("GET", "/api/qpid/status");
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Configure QPID_API_TOKEN."), (status, json!["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Only_get_head_and_post_are_allowed_and_every_answer_carries_the_security_headers()
    {
        using var api = ApiHarness.Start();
        foreach (var method in new[] { "PUT", "DELETE", "PATCH" })
        {
            var (status, json) = await api.Json(method, "/health");
            Assert.Equal((HttpStatusCode.MethodNotAllowed, "Método não permitido"), (status, json!["error"]!.GetValue<string>()));
        }
        var (_, healthText, health) = await api.Send("GET", "/health", token: null);
        Assert.Equal("{\"ok\":true,\"service\":\"odradek-scraper\"}", healthText);
        var (_, _, qpid) = await api.Send("GET", "/api/qpid/capabilities");
        foreach (var response in new[] { health, qpid })
        {
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("frame-ancestors 'none'; base-uri 'self'; form-action 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        }
        Assert.Equal("no-store", qpid.Headers.CacheControl?.ToString());
        var (_, databaseText, _) = await api.Send("GET", "/health/db", token: null);
        Assert.Equal("{\"ok\":true,\"service\":\"odradek-scraper\",\"database\":\"ok\"}", databaseText);
    }

    [Fact]
    public async Task Invalid_parameters_answer_422_like_fastapi()
    {
        using var api = ApiHarness.Start();
        foreach (var url in new[] { "/api/qpid/applications?limit=0", "/api/qpid/applications?limit=101", "/api/qpid/applications?limit=x", "/api/qpid/applications/abc?language=pt",
                     "/api/qpid/applications/1?language=es", "/api/qpid/queue?limit=501", "/api/qpid/queue?position=meio", "/api/qpid/logs?limit=0", "/api/qpid/applications/1/resume?language=fr" })
        {
            var (status, json) = await api.Json("GET", url);
            Assert.True(status == HttpStatusCode.UnprocessableEntity, url);
            Assert.NotNull(json!["detail"]![0]!["loc"]);
        }
        foreach (var body in new[] { "[]", "não é json", "" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.Json("POST", "/api/qpid/applications/1/attempt", body: body)).Status);
    }
}

public class ExtensionApiTests
{
    private static string IssueToken(ApiHarness api) => new ExtensionTokenActions(api.Temp.Db).Issue();

    [Fact]
    public async Task Extension_routes_only_answer_to_the_pairing_token()
    {
        using var api = ApiHarness.Start();
        foreach (var path in new[] { "/api/autofill-resume?language=pt" })
        {
            var (status, json) = await api.Json("GET", path, token: null);
            Assert.Equal((HttpStatusCode.Unauthorized, "Token da extensão inválido ou revogado. Gere outro no painel (Configurações)."), (status, json!["error"]!.GetValue<string>()));
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("GET", path, token: "odk_errado")).Status); // sem token configurado
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("GET", path, token: ApiHarness.QpidToken)).Status); // o token do Qpid não vale aqui
        }
        var token = IssueToken(api);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Json("GET", "/api/autofill-resume?language=pt", token)).Status); // autenticado, sem currículo
        new ExtensionTokenActions(api.Temp.Db).Revoke();
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("GET", "/api/autofill-resume?language=pt", token)).Status);
    }

    [Fact]
    public async Task Autofill_resume_serves_the_stored_pdf_or_explains_why_not()
    {
        using var api = ApiHarness.Start();
        var token = IssueToken(api);
        var (missingStatus, missing) = await api.Json("GET", "/api/autofill-resume?language=pt", token);
        Assert.Equal((HttpStatusCode.NotFound, "Currículo não cadastrado neste idioma."), (missingStatus, missing!["error"]!.GetValue<string>()));
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% teste\n");
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdf));
        using (var c = api.Temp.Db.Open())
        {
            c.Execute("INSERT INTO resumes(id,language,stored_path,file_sha256,created_at,updated_at) VALUES(1,'pt','nao-existe.pdf',@sha,'x','x')", new { sha });
            c.Execute("INSERT INTO resumes(id,language,stored_path,file_sha256,created_at,updated_at) VALUES(2,'en','nao-existe.pdf','0000','x','x')");
            c.Execute("INSERT INTO resume_files(resume_id,content) VALUES(1,@pdf)", new { pdf });
        }
        var (status, _, response) = await api.Send("GET", "/api/autofill-resume?language=pt", token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(pdf, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var (_, unreadable) = await api.Json("GET", "/api/autofill-resume?language=en", token);
        Assert.Equal("Arquivo de currículo indisponível.", unreadable!["error"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.Json("GET", "/api/autofill-resume?language=es", token)).Status);
    }

    private static FormUrlEncodedContent Form(string message, string? history = null) =>
        new(new Dictionary<string, string> { ["message"] = message, ["history"] = history ?? "[]" });

    private static async Task<(HttpStatusCode Status, JsonNode Json)> Post(ApiHarness api, string? token, HttpContent content)
    {
        var (status, text, _) = await api.Send("POST", "/api/assistant", token, content);
        return (status, JsonNode.Parse(text)!);
    }

    [Fact]
    public async Task The_assistant_answers_forms_and_maps_failures_to_the_python_status_codes()
    {
        using var api = ApiHarness.Start();
        var token = IssueToken(api);
        new SettingsStore(api.Temp.Db).Set("candidate_name", "Ana Exemplo");
        api.Ai.Gemini("Ana Exemplo");
        var (status, json) = await Post(api, token, Form("Qual é o seu nome?", "[{\"role\":\"user\",\"content\":\"oi\"}]"));
        Assert.Equal((HttpStatusCode.OK, "{\"ok\":true,\"answer\":\"Ana Exemplo\"}"), (status, json.ToJsonString()));
        Assert.Contains("User: oi", api.Ai.Requests[0].Body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());

        var multipart = new MultipartFormDataContent { { new StringContent("Qual é o seu nome?"), "message" } };
        api.Ai.Gemini("Ana Exemplo");
        Assert.Equal(HttpStatusCode.OK, (await Post(api, token, multipart)).Status);

        var (empty, emptyJson) = await Post(api, token, Form("   "));
        Assert.Equal((HttpStatusCode.BadRequest, "Cole a pergunta do formulário."), (empty, emptyJson["error"]!.GetValue<string>()));
        var (wrongType, wrongJson) = await Post(api, token, new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode.BadRequest, "Formulário inválido"), (wrongType, wrongJson["error"]!.GetValue<string>()));
        var (large, largeJson) = await Post(api, token, Form(new string('a', 300 * 1024)));
        Assert.Equal((HttpStatusCode.BadRequest, "Arquivo ou formulário muito grande"), (large, largeJson["error"]!.GetValue<string>()));

        api.AiKey = "";
        var (noKey, noKeyJson) = await Post(api, token, Form("oi"));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "IA indisponível: chave não configurada."), (noKey, noKeyJson["error"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(api, null, Form("oi"))).Status);
    }
}

public class QpidPanelTests
{
    [Fact]
    public async Task Queue_and_logs_list_the_tail_by_default_in_chronological_order()
    {
        using var api = ApiHarness.Start();
        var queue = new QueueActions(api.Temp.Db);
        var ids = new[] { queue.Enqueue("job_apply", [new("job_id", 1L)], "a"), queue.Enqueue("job_apply", [new("job_id", 2L)], "b"), queue.Enqueue("job_apply", [new("job_id", 3L)], "c") };
        using (var c = api.Temp.Db.Open()) c.Execute("UPDATE queue_jobs SET progress_log=@log, status='failed' WHERE id=@id", new { log = "primeiro\nÚLTIMO passo " + new string('x', 400), id = ids[2] });
        var (_, tail) = await api.Json("GET", "/api/qpid/queue?limit=2");
        Assert.Equal(ids.Skip(1).Select(i => (long)i), tail!["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()));
        Assert.Equal(1, tail["counts"]!["failed"]!.GetValue<long>());
        Assert.Equal(2, tail["counts"]!["pending"]!.GetValue<long>());
        var last = tail["items"]![1]!["last_step"]!.GetValue<string>();
        Assert.Equal(280, last.Length);
        Assert.StartsWith("ÚLTIMO passo ", last);
        var (_, head) = await api.Json("GET", "/api/qpid/queue?limit=2&position=inicio");
        Assert.Equal(ids.Take(2).Select(i => (long)i), head!["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()));

        var log = new EventLogStore(api.Temp.Db);
        foreach (var n in new[] { "um", "dois", "tres" }) log.Add("info", "teste", n);
        var (_, logs) = await api.Json("GET", "/api/qpid/logs?limit=2");
        var tailMessages = logs!["items"]!.AsArray().Select(i => i!["message"]!.GetValue<string>()).ToList();
        Assert.Equal(["dois", "tres"], tailMessages.TakeLast(2));
        Assert.Equal(["id", "created_at", "level", "source", "message"], ((JsonObject)logs["items"]![0]!).Select(kv => kv.Key));
        var (_, first) = await api.Json("GET", "/api/qpid/logs?limit=1&position=inicio");
        Assert.NotEqual("tres", first!["items"]![0]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Collector_start_and_stop_report_the_snapshot_and_validate_max_apply()
    {
        using var api = ApiHarness.Start();
        var (_, idle) = await api.Json("GET", "/api/qpid/status");
        Assert.Equal(["state", "message", "last_run", "last_found", "next_run_at", "max_apply"], ((JsonObject)idle!["collector"]!).Select(kv => kv.Key));
        Assert.Equal("stopped", idle["collector"]!["state"]!.GetValue<string>());

        foreach (var bad in new[] { "{\"max_apply\": 0}", "{\"max_apply\": 501}", "{\"max_apply\": 1.5}", "{\"max_apply\": true}", "{\"max_apply\": \"5\"}", "isso não é json" })
        {
            var (status, json) = await api.Json("POST", "/api/qpid/collector/start", body: bad);
            Assert.True(status == HttpStatusCode.BadRequest, bad);
            Assert.Contains(json!["error"]!.GetValue<string>(), new[] { "max_apply deve ser um inteiro de 1 a 500", "Corpo inválido" });
        }
        Assert.Equal("stopped", api.Collector.Snapshot().State);

        var (ok, started) = await api.Json("POST", "/api/qpid/collector/start", body: "{\"max_apply\": 7}");
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("Coleta iniciada.", started!["notice"]!.GetValue<string>());
        Assert.True(started["ok"]!.GetValue<bool>());
        var (_, again) = await api.Json("POST", "/api/qpid/collector/start");
        Assert.True(again!["notice"]!.GetValue<string>() is "A coleta já está em execução." or "Coleta iniciada."); // o ciclo sem fontes pode já ter terminado
        var (_, stopped) = await api.Json("POST", "/api/qpid/collector/stop");
        Assert.Equal("Solicitação para parar enviada.", stopped!["notice"]!.GetValue<string>());
        var (unknown, body) = await api.Json("POST", "/api/qpid/collector/pause");
        Assert.Equal((HttpStatusCode.NotFound, "Ação inválida"), (unknown, body!["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Bulk_ignore_previews_then_applies_only_when_the_count_is_still_the_one_checked()
    {
        using var api = ApiHarness.Start();
        var target = api.Temp.AddJob(status: "worth");
        api.Temp.AddJob(status: "worth");
        var criteria = "{\"job_id\": \"" + target + "\"}";
        var (_, preview) = await api.Json("POST", "/api/qpid/jobs/bulk-ignore/preview", body: criteria);
        Assert.Equal(1, preview!["count"]!.GetValue<int>());

        async Task<(HttpStatusCode, string)> Apply(string body)
        {
            var (s, j) = await api.Json("POST", "/api/qpid/jobs/bulk-ignore/apply", body: body);
            return (s, (j!["error"] ?? j["notice"])!.GetValue<string>());
        }
        Assert.Equal((HttpStatusCode.BadRequest, "Confira a quantidade antes de ignorar."), await Apply(criteria));
        Assert.Equal((HttpStatusCode.BadRequest, "Confira a quantidade antes de ignorar."), await Apply("{\"job_id\": \"" + target + "\", \"expected_count\": 0}"));
        Assert.Equal((HttpStatusCode.BadRequest, "Confira a quantidade antes de ignorar."), await Apply("{\"job_id\": \"" + target + "\", \"expected_count\": \"1\"}"));
        Assert.Equal((HttpStatusCode.Conflict, "A quantidade de vagas mudou. Confira novamente antes de ignorar."), await Apply("{\"job_id\": \"" + target + "\", \"expected_count\": 5}"));
        Assert.Equal((HttpStatusCode.BadRequest, "Filtros inválidos."), await Apply("{\"expected_count\": 1, \"extra\": \"x\"}"));
        Assert.Equal((HttpStatusCode.BadRequest, "Filtros inválidos."), await Apply("{\"expected_count\": 1, \"job_id\": " + target + "}"));
        Assert.Equal((HttpStatusCode.BadRequest, "Informe ao menos um filtro ou selecione todas."), await Apply("{\"expected_count\": 1}"));
        Assert.Equal("worth", api.Temp.Scalar<string>("SELECT status FROM jobs WHERE id=@target", new { target }));

        Assert.Equal((HttpStatusCode.OK, "1 vaga(s) ignorada(s)."), await Apply("{\"job_id\": \"" + target + "\", \"expected_count\": 1}"));
        Assert.Equal("ignored", api.Temp.Scalar<string>("SELECT status FROM jobs WHERE id=@target", new { target }));
        var (invalidStatus, invalid) = await api.Json("POST", "/api/qpid/jobs/bulk-ignore/preview", body: "{\"job_id\": \"abc\"}");
        Assert.Equal((HttpStatusCode.BadRequest, "O ID da vaga deve ser um número positivo."), (invalidStatus, invalid!["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Confirmed_applications_tell_the_open_window_to_refresh()
    {
        using var api = ApiHarness.Start();
        var job = api.Temp.AddJob(status: "worth");
        api.Temp.Changes.Clear();
        var (status, _) = await api.Json("POST", "/api/qpid/applications/" + job + "/confirmed", body: "{\"confirmed\": true}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(api.Temp.Changes, c => c is JobsChanged changed && changed.Ids.Contains(job));
        Assert.Equal("applied", api.Temp.Scalar<string>("SELECT status FROM jobs WHERE id=@job", new { job }));
    }
}

public class QpidTokenTests
{
    [Fact]
    public void The_environment_wins_then_the_vault_then_a_generated_token_then_the_file()
    {
        Assert.Equal("do-ambiente", QpidToken.Resolve(environment: "  do-ambiente "));
        Assert.Equal("do-cofre", QpidToken.Resolve(vaultGet: _ => "do-cofre", environment: ""));

        string? stored = null;
        var generated = QpidToken.Resolve(vaultGet: _ => "", vaultSet: (name, value) => stored = $"{name}={value}", environment: "");
        Assert.Equal($"qpid_api_token={generated}", stored);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", generated);

        var dir = Path.Combine(Path.GetTempPath(), "odradek-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string Broken(string _) => throw new InvalidOperationException("sem cofre");
            var first = QpidToken.Resolve(dir, Broken, environment: "");
            Assert.Equal(first, File.ReadAllText(Path.Combine(dir, "qpid_token.txt")));
            Assert.Equal(first, QpidToken.Resolve(dir, Broken, environment: "")); // o arquivo vale na próxima vez
        }
        finally { Directory.Delete(dir, true); }
    }
}
