using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Candidate;
using Odradek.Core.Collector;
using Odradek.Core.Data;
using Odradek.Core.Queue;

namespace Odradek.Core.Engines;

/// <summary>Sincronização do portfólio público (<c>sync_portfolio_now</c>). Nunca lança; registra no log do painel.</summary>
public sealed class PortfolioSync(Database db, HttpClient http)
{
    public async Task<PortfolioSyncResult> SyncNowAsync(bool force = false, CancellationToken ct = default)
    {
        var url = new SettingsStore(db).Get("portfolio_profile_url").Trim();
        var result = await PortfolioSource.SyncAsync(db, url, (u, token) => PortfolioSource.FetchAsync(http, u, 20, token), force, Clock.NowIso(), ct).ConfigureAwait(false);
        var log = new EventLogStore(db);
        if (result.Status == "updated") log.Add("success", "portfolio", result.Message);
        else if (result.Status == "error") log.Add("error", "portfolio", $"Falha ao sincronizar o portfólio: {result.Message}");
        return result;
    }
}

/// <summary>
/// Rotina diária (<c>_daily_worth_check_loop</c>, <c>app.py:659</c>): sincroniza o portfólio no boot e uma vez por dia e, depois das 03:00 de
/// Brasília, enfileira UMA verificação das vagas "vale a pena". A reivindicação do dia é atômica no banco.
/// </summary>
public sealed class DailyScheduler(Database db, Func<CancellationToken, Task> syncPortfolio, Func<int> purgeTerminal, Func<DateTime>? brasiliaNow = null)
{
    public const int CheckHour = 3;
    public const int RetentionDays = 14;

    private readonly Func<DateTime> _now = brasiliaNow ?? BrDates.BrasiliaNow;
    private readonly EventLogStore _log = new(db);
    private string _portfolioDay = "";
    private string _handledDay = ""; // dia já resolvido (por esta ou outra instância): não consulta o banco de novo até virar o dia

    private bool Claim(string today)
    {
        using var connection = db.Open();
        return connection.Execute("UPDATE settings SET value=@today WHERE key='worth_reverify_last_daily' AND value < @today", new { today }) == 1;
    }

    private void Release(string today)
    {
        using var connection = db.Open();
        connection.Execute("UPDATE settings SET value='' WHERE key='worth_reverify_last_daily' AND value=@today", new { today });
    }

    /// <summary>Uma passada da rotina. Em falha ao agendar, libera a reivindicação do dia (a próxima passada tenta de novo) e relança.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var now = _now();
        var today = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (today != _portfolioDay) // no boot e uma vez por dia; só reprocessa se a versão do portfólio mudou
        {
            _portfolioDay = today;
            await syncPortfolio(ct).ConfigureAwait(false);
        }
        var claimed = false;
        if (now.Hour >= CheckHour && today != _handledDay)
        {
            claimed = Claim(today);
            if (!claimed) _handledDay = today; // outra instância (ou execução anterior) já cuidou de hoje
        }
        if (!claimed) return;
        try
        {
            var purged = purgeTerminal();
            if (purged > 0) _log.Add("info", "queue", $"Retenção: {purged} item(ns) terminado(s) há mais de {RetentionDays} dias removido(s).");
        }
        catch (Exception) { /* a retenção nunca impede a verificação */ }
        int count;
        try
        {
            using var connection = db.Open();
            count = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM jobs WHERE status='worth'");
        }
        catch (Exception) { Release(today); throw; }
        if (count > 0)
        {
            long queueId;
            try { queueId = new QueueActions(db).Enqueue(QueueEngine.KindWorthReverify, [new("scheduled_date", today)], $"daily:{today}"); }
            catch (Exception) { Release(today); throw; }
            _log.Add("info", "worth-check", $"Verificação diária enfileirada (fila #{queueId}, {count} vaga(s)).");
        }
        else _log.Add("info", "worth-check", "Verificação diária: nenhuma vaga para checar.");
        _handledDay = today;
    }

    /// <summary>Repete a rotina a cada minuto até o cancelamento; uma falha não derruba o laço.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.Add("warning", "worth-check", $"Falha ao agendar verificação diária de vagas: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
