using System.Globalization;
using Dapper;
using Odradek.Core.Data;

namespace Odradek.Core.Actions;

/// <summary>Filtros do "Ignorar vagas em revisão por critérios" (<c>jobs/bulk_ignore.py</c>).</summary>
public sealed record BulkIgnoreCriteria(string JobId, string Country, string FoundFrom, string FoundTo, string PostedFrom, string PostedTo,
    string MatchMin, string MatchMax, bool All)
{
    public static readonly BulkIgnoreCriteria Empty = new("", "", "", "", "", "", "", "", false);

    private static bool IsIsoDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Normaliza (trim) e valida; lança <see cref="ActionException"/> com a mesma mensagem do Python.</summary>
    public BulkIgnoreCriteria Validated()
    {
        var c = this with
        {
            JobId = JobId.Trim(), Country = Country.Trim(), FoundFrom = FoundFrom.Trim(), FoundTo = FoundTo.Trim(),
            PostedFrom = PostedFrom.Trim(), PostedTo = PostedTo.Trim(), MatchMin = MatchMin.Trim(), MatchMax = MatchMax.Trim(),
        };
        if (c.JobId.Length > 0 && (!Validate.IsDigits(c.JobId) || !Validate.InRange(c.JobId, 1, 999_999_999)))
            throw new ActionException("O ID da vaga deve ser um número positivo.");
        if (Validate.Length(c.Country) > 100) throw new ActionException("Nome do país muito longo.");
        foreach (var date in new[] { c.FoundFrom, c.FoundTo, c.PostedFrom, c.PostedTo })
            if (date.Length > 0 && !IsIsoDate(date)) throw new ActionException("Use datas no formato AAAA-MM-DD.");
        if (c.FoundFrom.Length > 0 && c.FoundTo.Length > 0 && string.CompareOrdinal(c.FoundFrom, c.FoundTo) > 0 ||
            c.PostedFrom.Length > 0 && c.PostedTo.Length > 0 && string.CompareOrdinal(c.PostedFrom, c.PostedTo) > 0)
            throw new ActionException("A data inicial deve ser anterior à final.");
        foreach (var match in new[] { c.MatchMin, c.MatchMax })
            if (match.Length > 0 && !Validate.InRange(match, 0, 100)) throw new ActionException("O match deve estar entre 0 e 100.");
        if (c.MatchMin.Length > 0 && c.MatchMax.Length > 0 && int.Parse(c.MatchMin) > int.Parse(c.MatchMax))
            throw new ActionException("O match mínimo deve ser menor ou igual ao máximo.");
        var any = new[] { c.JobId, c.Country, c.FoundFrom, c.FoundTo, c.PostedFrom, c.PostedTo, c.MatchMin, c.MatchMax }.Any(v => v.Length > 0);
        if (c.All && any) throw new ActionException("Use 'todas' sem outros filtros.");
        if (!c.All && !any) throw new ActionException("Informe ao menos um filtro ou selecione todas.");
        return c;
    }
}

/// <summary>Ações sobre vagas: status, anotações, conferência e ignorar em lote. Cada uma publica <see cref="JobsChanged"/> depois do commit.</summary>
public sealed class JobActions(Database db)
{
    private static readonly HashSet<string> Statuses = ["applied", "ignored", "saved", "worth"];

    public string SetStatus(long id, string status)
    {
        if (!Statuses.Contains(status)) throw new ActionException("Status inválido");
        using (var connection = db.Open())
        {
            if (connection.ExecuteScalar<long?>("SELECT id FROM jobs WHERE id=@id", new { id }) is null) throw new ActionException("Vaga não encontrada");
            connection.Execute("UPDATE jobs SET status=@status, applied_at=COALESCE(@applied, applied_at) WHERE id=@id",
                new { status, applied = status == "applied" ? Clock.NowIso() : null, id });
        }
        db.Bus.Publish(new JobsChanged([id]));
        return "Status atualizado.";
    }

    public string SetNotes(long id, string notes)
    {
        if (Validate.Length(notes) > 30000) throw new ActionException("Nota inválida");
        using (var connection = db.Open())
        {
            if (connection.ExecuteScalar<long?>("SELECT id FROM jobs WHERE id=@id", new { id }) is null) throw new ActionException("Vaga não encontrada");
            connection.Execute("UPDATE jobs SET notes=@notes WHERE id=@id", new { notes, id });
        }
        db.Bus.Publish(new JobsChanged([id]));
        return "Anotação salva.";
    }

    /// <summary>"Marcar como conferida": de 1 a 100 vagas ignoradas pela IA.</summary>
    public string MarkReviewed(IReadOnlyList<long> ids)
    {
        if (ids.Count is < 1 or > 100) throw new ActionException("Informe de 1 a 100 vagas.");
        var count = new PanelQueries(db).MarkReviewed(ids);
        return $"{count} vaga(s) marcada(s) como conferida(s).";
    }

    private static (string Where, DynamicParameters Params) BulkWhere(BulkIgnoreCriteria c)
    {
        var clauses = new List<string> { "status = 'worth'" };
        var p = new DynamicParameters();
        if (c.JobId.Length > 0) { clauses.Add("id = @id"); p.Add("id", long.Parse(c.JobId)); }
        if (c.Country.Length > 0)
        {
            var country = c.Country.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            clauses.Add("LOWER(location) LIKE @country ESCAPE '\\'");
            p.Add("country", $"%{country}%");
        }
        var (lo, hi) = JobSort.DateBoundsUtc(c.FoundFrom, c.FoundTo);
        if (lo is not null) { clauses.Add("first_seen_at >= @lo"); p.Add("lo", lo); }
        if (hi is not null) { clauses.Add("first_seen_at < @hi"); p.Add("hi", hi); }
        if (c.PostedFrom.Length > 0) { clauses.Add("SUBSTR(posted_at, 1, 10) >= @pf"); p.Add("pf", c.PostedFrom); }
        if (c.PostedTo.Length > 0) { clauses.Add("SUBSTR(posted_at, 1, 10) <= @pt"); p.Add("pt", c.PostedTo); }
        if (c.MatchMin.Length > 0) { clauses.Add($"COALESCE({JobSort.MatchExpr}, 0) >= @mmin"); p.Add("mmin", int.Parse(c.MatchMin)); }
        if (c.MatchMax.Length > 0) { clauses.Add($"COALESCE({JobSort.MatchExpr}, 0) <= @mmax"); p.Add("mmax", int.Parse(c.MatchMax)); }
        return (string.Join(" AND ", clauses), p);
    }

    public int BulkIgnorePreview(BulkIgnoreCriteria criteria)
    {
        var (where, p) = BulkWhere(criteria.Validated());
        using var connection = db.Open();
        return connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM jobs WHERE {where}", p);
    }

    /// <summary>Ignora as vagas dos critérios, só se a contagem ainda for a que o usuário conferiu (<paramref name="expectedCount"/>).</summary>
    public string BulkIgnoreApply(BulkIgnoreCriteria criteria, int expectedCount)
    {
        if (expectedCount < 1) throw new ActionException("Confira a quantidade antes de ignorar.");
        var valid = criteria.Validated();
        var (where, p) = BulkWhere(valid);
        int count;
        using (var connection = db.Open())
        using (var tx = connection.BeginTransaction())
        {
            // contagem e atualização na mesma transação: ninguém muda o conjunto entre a conferência e o UPDATE
            if (connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM jobs WHERE {where}", p, tx) != expectedCount)
                throw new ActionException("A quantidade de vagas mudou. Confira novamente antes de ignorar.");
            count = connection.Execute($"UPDATE jobs SET status='ignored' WHERE {where}", p, tx);
            tx.Commit();
        }
        db.Bus.Publish(new JobsChanged([]));
        return $"{count} vaga(s) ignorada(s).";
    }
}
