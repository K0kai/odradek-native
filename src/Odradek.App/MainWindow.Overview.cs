using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Odradek.Core.Data;

namespace Odradek.App;

/// <summary>"Visão geral": contagem por status (cada bloco abre a lista), últimas coletas e vagas recentes.</summary>
public partial class MainWindow
{
    private async Task LoadOverviewAsync()
    {
        try
        {
            var data = await Task.Run(() => new PanelQueries(_database).Dashboard());
            Dispatcher.UIThread.Post(() => { if (_page == Page.Overview) RenderOverview(data); });
        }
        catch (Exception ex) { Program.Log("Visão geral: " + ex.Message); }
    }

    private static TextBlock Heading(string text) => new() { Text = text, Classes = { "h2" }, Margin = new Thickness(0, 6, 0, 0) };

    private void RenderOverview(DashboardData data)
    {
        OverviewHost.Children.Clear();
        OverviewHost.Children.Add(Heading("Vagas por status"));
        var tiles = new WrapPanel();
        foreach (var (key, label) in Defaults.Statuses)
        {
            var count = data.Counts.TryGetValue(key, out var n) ? n : 0;
            var number = new TextBlock { Text = count.ToString(), FontSize = 26, FontWeight = FontWeight.SemiBold };
            var tile = Ui.Card(new StackPanel { Spacing = 2, Children = { number, new TextBlock { Text = label, Classes = { "muted" } } } });
            tile.MinWidth = 150; tile.Margin = new Thickness(0, 0, 10, 10); tile.Padding = new Thickness(14, 10);
            tile.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(tile, $"Abrir {label}");
            var statusKey = key;
            tile.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) SelectStatus(statusKey); };
            tiles.Children.Add(tile);
        }
        OverviewHost.Children.Add(tiles);

        OverviewHost.Children.Add(Heading("Últimas coletas"));
        var runs = new StackPanel { Spacing = 6 };
        foreach (var run in data.Runs)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,110,90,*") };
            row.Children.Add(new TextBlock { Text = Format.Date(run.StartedAt, true), Classes = { "muted" } });
            row.Children.Add(Cell(1, RunState(run.State)));
            row.Children.Add(Cell(2, $"{run.FoundCount} vaga(s)"));
            var message = Cell(3, run.Message); ((TextBlock)message).TextTrimming = TextTrimming.CharacterEllipsis; ((TextBlock)message).Classes.Add("muted");
            row.Children.Add(message);
            runs.Children.Add(row);
        }
        if (data.Runs.Count == 0) runs.Children.Add(new TextBlock { Text = "Nenhuma coleta registrada ainda.", Classes = { "muted" } });
        OverviewHost.Children.Add(Ui.Card(runs));

        OverviewHost.Children.Add(Heading("Vagas recentes"));
        var recent = new StackPanel { Spacing = 6 };
        foreach (var job in data.Jobs)
        {
            var label = Defaults.Statuses.FirstOrDefault(s => s.Key == job.Status).Label ?? job.Status;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var text = new StackPanel { Children = { new TextBlock { Text = job.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = $"{job.Company} · {job.Location}", Classes = { "muted" }, TextTrimming = TextTrimming.CharacterEllipsis } } };
            row.Children.Add(text);
            var badgeText = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeight.SemiBold };
            var badge = new Border { Classes = { "badge" }, Child = badgeText, Margin = new Thickness(10, 0, 0, 0) };
            Ui.Paint(badge, badgeText, job.Status == "worth" ? "WarningSoft" : job.Status == "applied" ? "SuccessSoft" : "NeutralSoft", job.Status == "worth" ? "Warning" : job.Status == "applied" ? "Success" : "Muted");
            Grid.SetColumn(badge, 1);
            row.Children.Add(badge);
            recent.Children.Add(row);
        }
        if (data.Jobs.Count == 0) recent.Children.Add(new TextBlock { Text = "Nenhuma vaga coletada ainda.", Classes = { "muted" } });
        OverviewHost.Children.Add(Ui.Card(recent));
    }

    private static TextBlock Cell(int column, string text)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(block, column);
        return block;
    }

    private static string RunState(string state) => state switch
    {
        "running" => "em andamento", "finished" or "completed" or "done" => "concluída", "stopped" => "parada", "failed" or "error" => "com falha", _ => state,
    };
}
