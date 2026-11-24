using Odradek.Core.Data;

namespace Odradek.Core.Tests;

/// <summary>A lista de vagas do painel: um status, ou todos de uma vez.</summary>
public class JobListTests
{
    [Fact]
    public void The_all_option_lists_every_status_and_still_honours_the_other_filters()
    {
        using var t = new TempDb();
        var worth = t.AddJob("worth", score: 90);
        var applied = t.AddJob("applied", score: 40);
        var ignored = t.AddJob("ignored", seen: "2026-10-01T12:00:00+00:00");
        var queries = new PanelQueries(t.Db);

        var all = queries.Jobs(1, 50, PanelQueries.AllStatuses);
        Assert.Equal(3, all.Total);
        Assert.Equal(new HashSet<long> { worth, applied, ignored }, all.Items.Select(j => j.Id).ToHashSet());
        Assert.Equal(["worth", "applied", "ignored"], all.Items.OrderBy(j => new[] { worth, applied, ignored }.ToList().IndexOf(j.Id)).Select(j => j.Status)); // cada vaga mantém o próprio status
        Assert.Equal(PanelQueries.AllStatuses, all.Filters.Status);

        Assert.Equal([worth], queries.Jobs(1, 50, PanelQueries.AllStatuses, minMatch: 80).Items.Select(j => j.Id)); // nota mínima
        Assert.Equal([ignored], queries.Jobs(1, 50, PanelQueries.AllStatuses, dateTo: "2026-10-31").Items.Select(j => j.Id)); // intervalo de datas
        Assert.Equal(1, queries.Jobs(1, 50, "applied").Total); // um status específico continua filtrando
        Assert.Equal(1, queries.Jobs(1, 50, "não existe").Total); // inválido continua caindo em "Vale a pena olhar"
    }
}
