using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Odradek.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };

    /// <summary>
    /// Filtros da lista de vagas: a busca age enquanto se digita (espera 350 ms de pausa), as datas vêm de seletores de calendário (o banco
    /// continua recebendo AAAA-MM-DD) e "Limpar filtros" aparece quando há algum filtro ativo.
    /// </summary>
    private void WireFilterFields()
    {
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplySearch(); };
        SearchBox.TextChanged += (_, _) =>
        {
            SearchClearButton.IsVisible = !string.IsNullOrEmpty(SearchBox.Text);
            _searchDebounce.Stop(); _searchDebounce.Start();
        };
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _searchDebounce.Stop(); ApplySearch(); e.Handled = true; }
            else if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text)) { SearchBox.Text = ""; e.Handled = true; }
        };
        SearchClearButton.Click += (_, _) => { SearchBox.Text = ""; SearchBox.Focus(); };
        DateFromBox.SelectedDateChanged += (_, _) => { _dateFrom = DateFromBox.SelectedDate?.ToString("yyyy-MM-dd") ?? ""; FilterChanged(); };
        DateToBox.SelectedDateChanged += (_, _) => { _dateTo = DateToBox.SelectedDate?.ToString("yyyy-MM-dd") ?? ""; FilterChanged(); };
        MinBox.SelectionChanged += (_, _) => UpdateFilterChrome();
        ClearFiltersButton.Click += (_, _) =>
        {
            _searchDebounce.Stop();
            SearchBox.Text = ""; DateFromBox.SelectedDate = null; DateToBox.SelectedDate = null; MinBox.SelectedIndex = 0;
            _query = ""; _dateFrom = ""; _dateTo = ""; _minMatch = 0;
            FilterChanged();
        };
    }

    private void ApplySearch()
    {
        var text = SearchBox.Text?.Trim() ?? "";
        if (text == _query) return;
        _query = text;
        FilterChanged();
    }

    private void FilterChanged()
    {
        _pageNumber = 1;
        UpdateFilterChrome();
        _ = ReloadJobsAsync();
    }

    private void UpdateFilterChrome() =>
        ClearFiltersButton.IsVisible = _minMatch > 0 || _dateFrom != "" || _dateTo != "" || (_query != "" && _status.StartsWith("triage:", StringComparison.Ordinal));
}
