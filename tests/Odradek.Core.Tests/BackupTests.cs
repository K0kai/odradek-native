using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using Odradek.Core.Backup;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

public class PostgresUrlTests
{
    [Fact]
    public void A_neon_style_url_becomes_npgsql_parameters()
    {
        var b = PostgresUrl.Parse("postgresql://user:p%40ss%3Aword@ep-cool-123.us-east-2.aws.neon.tech/neondb?sslmode=require");
        Assert.Equal("ep-cool-123.us-east-2.aws.neon.tech", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("neondb", b.Database);
        Assert.Equal("user", b.Username);
        Assert.Equal("p@ss:word", b.Password);
        Assert.Equal(SslMode.Require, b.SslMode);
    }

    [Fact]
    public void A_supabase_pooler_url_keeps_its_port_and_defaults_to_prefer()
    {
        var b = PostgresUrl.Parse("postgres://postgres.abc:secret@aws-0-sa-east-1.pooler.supabase.com:6543/postgres");
        Assert.Equal(6543, b.Port);
        Assert.Equal("postgres.abc", b.Username);
        Assert.Equal(SslMode.Prefer, b.SslMode);
    }

    [Fact]
    public void Verify_full_validates_the_certificate_and_connect_timeout_is_honored()
    {
        var b = PostgresUrl.Parse("postgresql://u:p@h/d?sslmode=verify-full&connect_timeout=7");
        Assert.Equal(SslMode.VerifyFull, b.SslMode);
        Assert.Equal(7, b.Timeout);
    }

    [Theory]
    [InlineData("mysql://u:p@h/d")]
    [InlineData("postgresql://")]
    [InlineData("not a url")]
    [InlineData("postgresql://u:p@h/d?sslmode=bogus")]
    public void Invalid_urls_are_rejected_without_echoing_the_url(string url)
    {
        var error = Assert.Throws<BackupException>(() => PostgresUrl.Parse(url));
        Assert.DoesNotContain("u:p", error.Message);
    }
}

public class DbCopyTests
{
    [Fact]
    public void The_embedded_schema_splits_into_one_statement_per_table_and_index()
    {
        var statements = DbCopy.SchemaStatements();
        Assert.All(statements, s => Assert.StartsWith("CREATE", s));
        Assert.All(statements, s => Assert.DoesNotContain("--", s.Split('\n')[0]));
        foreach (var table in DbCopy.TableOrder) Assert.Contains(statements, s => s.StartsWith($"CREATE TABLE IF NOT EXISTS {table} "));
    }

    [Theory]
    [InlineData(5L, "text", "5")]
    [InlineData(1.5, "text", "1.5")]
    [InlineData("a\0b", "text", "ab")]
    [InlineData("12", "bigint", 12L)]
    [InlineData(3.0, "integer", 3L)]
    [InlineData(2L, "double precision", 2.0)]
    public void Values_are_converted_to_the_column_type(object value, string type, object expected) => Assert.Equal(expected, DbCopy.ToPostgres(value, type));

    [Fact]
    public void Nulls_stay_null_and_bytes_stay_bytes()
    {
        Assert.Equal(DBNull.Value, DbCopy.ToPostgres(null, "text"));
        Assert.Equal(new byte[] { 1, 2 }, DbCopy.ToPostgres(new byte[] { 1, 2 }, "bytea"));
    }

    [Fact]
    public void The_snapshot_is_a_consistent_copy_that_the_source_can_outlive()
    {
        using var temp = new TempDb();
        temp.AddJob(); temp.AddJob("applied");
        var folder = Path.Combine(Path.GetTempPath(), "odradek-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = DbCopy.CreateSnapshot(temp.Db, folder);
            temp.AddJob(); // o app continua gravando: o instantâneo não muda
            using var snapshot = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            snapshot.Open();
            Assert.Equal(2, snapshot.ExecuteScalar<long>("SELECT COUNT(*) FROM jobs"));
            Assert.Equal(3, temp.Scalar<long>("SELECT COUNT(*) FROM jobs"));
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    [Fact]
    public void A_missing_local_database_is_reported_in_portuguese()
    {
        var db = new Database(Path.Combine(Path.GetTempPath(), "odradek-none-" + Guid.NewGuid().ToString("N"), "jobs.db"));
        Assert.Throws<BackupException>(() => DbCopy.CreateSnapshot(db, Path.GetTempPath()));
    }
}

public class BackupServiceTests
{
    private const string Url = "postgresql://someone:hunter2@db.example.com/prod";

    private sealed class Rig : IDisposable
    {
        public TempDb Temp { get; } = new();
        public int Copies;
        public bool Fail;
        public string Url { get; set; } = BackupServiceTests.Url;
        public DateTime Brasilia = new(2026, 11, 24, 3, 0, 0);
        public DateTime Utc = new(2026, 11, 24, 6, 0, 0, DateTimeKind.Utc);
        public BackupService Service { get; }

        public Rig(Func<string, CancellationToken, Task>? gate = null)
        {
            Service = new BackupService(Temp.Db, () => Url,
                async (target, _, ct) =>
                {
                    Copies++;
                    if (gate is not null) await gate(target, ct);
                    if (Fail) throw new NpgsqlException("Resource temporarily unavailable (hunter2@db.example.com:5432)", new TimeoutException());
                    return new Dictionary<string, int> { ["jobs"] = 3, ["settings"] = 40 };
                },
                () => Brasilia, () => Utc);
        }

        public string Setting(string key) => Temp.Scalar<string>("SELECT value FROM settings WHERE key=@key", new { key });
        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public async Task Without_a_url_nothing_runs()
    {
        using var rig = new Rig { Url = "" };
        Assert.False(rig.Service.Configured);
        Assert.Null(await rig.Service.RunAsync());
        Assert.Equal(0, rig.Copies);
    }

    [Fact]
    public async Task A_good_backup_is_recorded_in_the_python_format_and_logged()
    {
        using var rig = new Rig();
        var result = await rig.Service.RunAsync();
        Assert.True(result!.Ok);
        var stored = (JsonObject)JsonNode.Parse(rig.Setting("standby_last_sync_json"))!;
        Assert.Equal(["ok", "at", "error", "tables", "rows", "seconds"], stored.Select(kv => kv.Key));
        Assert.Equal(43, stored["rows"]!.GetValue<int>());
        Assert.Equal(3, stored["tables"]!["jobs"]!.GetValue<int>());
        Assert.Equal("2026-11-24T06:00:00+00:00", stored["at"]!.GetValue<string>());
        var usage = UsageReader.Read(rig.Temp.Db).Backup!;
        Assert.True(usage.Ok);
        Assert.Equal(43, usage.Rows);
        Assert.Contains("Standby atualizado: 43 linha(s)", rig.Temp.Scalar<string>("SELECT message FROM event_logs WHERE source='standby' ORDER BY id DESC LIMIT 1"));
    }

    [Fact]
    public async Task A_failed_backup_never_throws_and_never_leaks_the_connection_details()
    {
        using var rig = new Rig { Fail = true };
        var result = await rig.Service.RunAsync();
        Assert.False(result!.Ok);
        Assert.Equal("Banco de dados indisponível: tempo esgotado ao conectar.", result.Error);
        var everything = rig.Setting("standby_last_sync_json") + rig.Temp.Scalar<string>("SELECT group_concat(message) FROM event_logs");
        Assert.DoesNotContain("hunter2", everything);
        Assert.DoesNotContain("db.example.com", everything);
    }

    [Fact]
    public async Task A_url_that_is_not_postgres_is_refused_before_copying()
    {
        using var rig = new Rig { Url = "sqlite:C:/x.db" };
        var result = await rig.Service.RunAsync();
        Assert.False(result!.Ok);
        Assert.Equal(0, rig.Copies);
    }

    [Fact]
    public async Task Two_backups_never_run_at_the_same_time()
    {
        var release = new TaskCompletionSource();
        var running = 0; var peak = 0;
        using var rig = new Rig(async (_, _) =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref running));
            await release.Task;
            Interlocked.Decrement(ref running);
        });
        var first = rig.Service.RunAsync();
        var second = rig.Service.RunAsync();
        await Task.Delay(100);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, peak);
        Assert.Equal(2, rig.Copies);
    }

    [Fact]
    public async Task The_daily_backup_waits_for_4am_runs_once_and_is_claimed_for_the_day()
    {
        using var rig = new Rig();
        await rig.Service.TickAsync();
        Assert.Equal(0, rig.Copies); // 03:00: cedo
        rig.Brasilia = new DateTime(2026, 11, 24, 4, 5, 0);
        await rig.Service.TickAsync();
        await rig.Service.TickAsync();
        Assert.Equal(1, rig.Copies);
        Assert.Equal("2026-11-24", rig.Setting("standby_sync_last_daily"));
        rig.Brasilia = new DateTime(2026, 11, 25, 4, 0, 0);
        await rig.Service.TickAsync();
        Assert.Equal(2, rig.Copies);
    }

    [Fact]
    public async Task Another_instance_that_already_claimed_today_stops_this_one()
    {
        using var rig = new Rig();
        using (var c = rig.Temp.Db.Open()) c.Execute("UPDATE settings SET value='2026-11-24' WHERE key='standby_sync_last_daily'");
        rig.Brasilia = new DateTime(2026, 11, 24, 5, 0, 0);
        await rig.Service.TickAsync();
        Assert.Equal(0, rig.Copies);
    }

    [Fact]
    public async Task A_failed_daily_backup_is_released_and_retried_one_hour_later()
    {
        using var rig = new Rig { Fail = true };
        rig.Brasilia = new DateTime(2026, 11, 24, 4, 0, 0);
        await rig.Service.TickAsync();
        Assert.Equal(1, rig.Copies);
        Assert.Equal("", rig.Setting("standby_sync_last_daily")); // liberado para a próxima tentativa
        rig.Utc += TimeSpan.FromMinutes(30);
        await rig.Service.TickAsync();
        Assert.Equal(1, rig.Copies); // ainda dentro da hora de espera
        rig.Utc += TimeSpan.FromMinutes(31);
        rig.Fail = false;
        await rig.Service.TickAsync();
        Assert.Equal(2, rig.Copies);
        Assert.Equal("2026-11-24", rig.Setting("standby_sync_last_daily"));
    }

    [Fact]
    public async Task The_exit_backup_is_skipped_when_a_recent_one_exists_and_runs_otherwise()
    {
        using var rig = new Rig();
        Assert.NotNull(await rig.Service.BackupOnExitAsync());
        Assert.Equal(1, rig.Copies);
        rig.Utc += TimeSpan.FromMinutes(5);
        Assert.Null(await rig.Service.BackupOnExitAsync());
        Assert.Equal(1, rig.Copies);
    }

    [Fact]
    public async Task The_exit_backup_ignores_a_failed_last_backup_and_a_stale_one()
    {
        using var rig = new Rig { Fail = true };
        await rig.Service.RunAsync();
        rig.Fail = false;
        Assert.NotNull(await rig.Service.BackupOnExitAsync()); // a última falhou: tenta de novo
        Assert.Equal(2, rig.Copies);
        rig.Utc += TimeSpan.FromHours(2);
        Assert.NotNull(await rig.Service.BackupOnExitAsync()); // a última tem mais de 30 min
        Assert.Equal(3, rig.Copies);
    }

    [Fact]
    public async Task The_exit_backup_gives_up_after_the_time_limit_and_does_not_hang()
    {
        using var rig = new Rig(async (_, ct) => await Task.Delay(Timeout.Infinite, ct));
        var started = DateTime.UtcNow;
        Assert.Null(await rig.Service.BackupOnExitAsync(TimeSpan.FromMilliseconds(200)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Contains("limite de tempo", rig.Temp.Scalar<string>("SELECT message FROM event_logs WHERE source='standby' ORDER BY id DESC LIMIT 1"));
    }

    [Theory]
    [InlineData("password authentication failed for user \"x\"", "usuário ou senha")]
    [InlineData("too many connections for role", "limite de conexões")]
    [InlineData("exceeded the data transfer quota", "cota do Neon")]
    [InlineData("Name or service not known", "host da URL não foi encontrado")]
    [InlineData("something odd", "não foi possível conectar")]
    public void Connection_errors_are_classified_without_the_raw_text(string raw, string expected)
    {
        var message = BackupService.Describe(new NpgsqlException(raw));
        Assert.Contains(expected, message);
        Assert.DoesNotContain(raw, message);
    }

    [Fact]
    public void Unknown_failures_only_report_their_type() => Assert.Equal("InvalidOperationException", BackupService.Describe(new InvalidOperationException("host=secret")));
}
