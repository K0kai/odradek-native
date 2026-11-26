namespace Odradek.Core.Data;

/// <summary>
/// Barramento de mudanças em processo: substitui o SSE do app Python. Quem grava publica <em>depois</em> do commit, e as telas
/// recebem o que mudou (ids, linha de log) em vez de um "algo mudou" seguido de uma consulta completa.
/// Os assinantes rodam na thread de quem publicou: a tela deve passar para a thread de interface.
/// </summary>
public sealed class ChangeBus
{
    public static ChangeBus Shared { get; } = new();

    public event Action<Change>? Published;

    public void Publish(Change change)
    {
        var handlers = Published;
        if (handlers is null) return; // sem ninguém ouvindo o custo é zero
        foreach (var handler in handlers.GetInvocationList().Cast<Action<Change>>())
        {
            try { handler(change); }
            catch (Exception) { /* um assinante com defeito não pode impedir a gravação nem os outros */ }
        }
    }
}

public abstract record Change;

/// <summary>Vagas criadas ou alteradas (status, notas, revisão). Lista vazia = muita coisa mudou, recarregue.</summary>
public sealed record JobsChanged(IReadOnlyList<long> Ids) : Change;

/// <summary>Item da fila criado ou alterado; <c>null</c> = a fila inteira mudou.</summary>
public sealed record QueueChanged(long? Id) : Change;

/// <summary>A fila em segundo plano das vagas do LinkedIn mudou (entrou ou saiu vaga, começou ou acabou uma espera).</summary>
public sealed record LinkedInQueueChanged : Change;

/// <summary>O envio do Indeed pela extensão mudou (vagas anunciadas ou recebidas, ou a extensão parou).</summary>
public sealed record IndeedQueueChanged : Change;

public sealed record LogAppended(LogEntry Entry) : Change;

public sealed record LogsCleared : Change;

public sealed record SettingsChanged(IReadOnlyCollection<string> Keys) : Change;

public sealed record ResumesChanged : Change;

public sealed record CollectorChanged : Change;

public sealed record QuotaChanged : Change;

public sealed record LogEntry(long Id, string CreatedAt, string Level, string Source, string Message);
