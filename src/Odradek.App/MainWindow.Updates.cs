using Avalonia.Controls;
using Avalonia.Threading;
using Odradek.Core.Queue;

namespace Odradek.App;

public partial class MainWindow
{
    private UpdateCoordinator? _updates;
    private bool _confirmingUpdate;
    private UpdateNote? _updateNote; // resultado da última procura pedida pelo botão (some depois de alguns segundos)

    /// <summary>Liga o cartão de atualização da barra lateral (só existe no app instalado, com a atualização automática ligada).</summary>
    internal void AttachUpdates(UpdateCoordinator updates)
    {
        _updates = updates;
        UpdateBox.IsVisible = true;
        updates.Changed += () => Dispatcher.UIThread.Post(() => { _confirmingUpdate = false; _updateNote = null; RenderUpdate(); });
        UpdateButton.Click += async (_, _) => await OnUpdateClickAsync();
        RenderUpdate();
    }

    private void RenderUpdate()
    {
        if (_updates is null) return;
        var current = _updates.Updater.Current.ToString(3);
        if (_updates.Staged is { } staged)
        {
            UpdateText.Text = _confirmingUpdate ? "Interrompe o que está rodando" : $"Nova versão {staged.Version.ToString(3)}";
            ToolTip.SetTip(UpdateText, _confirmingUpdate
                ? "Há trabalho em andamento (fila, coleta ou backup): ele será interrompido e retomado depois."
                : $"Versão {staged.Version.ToString(3)} pronta para instalar (você usa a {current}). O Odradek fecha e abre de novo.");
            UpdateButton.Content = _confirmingUpdate ? "Confirmar" : "Atualizar";
            UpdateButton.Classes.Set("ready", true);
        }
        else
        {
            UpdateText.Text = _updateNote?.Short ?? ""; // a versão em uso tem a sua própria linha (VersionText)
            ToolTip.SetTip(UpdateText, _updateNote?.Full ?? $"Você usa a versão {current}.");
            UpdateButton.Content = "Buscar atualização";
            UpdateButton.Classes.Set("ready", false);
        }
    }

    /// <summary>Resultado de uma procura pedida pelo botão: curto na linha, completo na dica.</summary>
    private sealed record UpdateNote(string Short, string Full);

    private static UpdateNote NoteFor(UpdateCheck result) => new(result switch
    {
        UpdateCheck.UpToDate => "Você está em dia",
        UpdateCheck.FeedUnavailable => "Sem acesso ao feed",
        UpdateCheck.Failed => "Falha ao preparar",
        _ => "",
    }, UpdateCoordinator.Describe(result));

    private bool WorkInProgress() =>
        QueueRuntime.RunningIds(Program.Database).Count > 0
        || Program.Engines.Collector.Snapshot().State is "running" or "stopping"
        || Program.Engines.Backup.IsRunning;

    private async Task OnUpdateClickAsync()
    {
        if (_updates is null || !UpdateButton.IsEnabled) return;
        if (_updates.Staged is not null)
        {
            if (!_confirmingUpdate && WorkInProgress()) { _confirmingUpdate = true; RenderUpdate(); return; } // 2º clique confirma, como as outras ações destrutivas
            UpdateButton.IsEnabled = false;
            UpdateText.Text = "Reiniciando…";
            if (Avalonia.Application.Current is App app) await app.QuitAsync(restartForUpdate: true, background: false);
            // Se chegou aqui sem fechar, a versão preparada deixou de valer.
            _updates.Revalidate();
            _confirmingUpdate = false;
            UpdateButton.IsEnabled = true;
            RenderUpdate();
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateText.Text = "Procurando…";
        _updateNote = null;
        try { _updateNote = NoteFor(await _updates.CheckNowAsync()); }
        catch (Exception ex) { Program.Log("Update: falha ao procurar: " + ex); _updateNote = new("Não consegui procurar", "Não foi possível procurar atualizações."); }
        finally { UpdateButton.IsEnabled = true; RenderUpdate(); }
        var note = _updateNote;
        DispatcherTimer.RunOnce(() => { if (ReferenceEquals(_updateNote, note)) { _updateNote = null; RenderUpdate(); } }, TimeSpan.FromSeconds(6));
    }
}
