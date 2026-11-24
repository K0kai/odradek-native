namespace Odradek.Core.Ai;

/// <summary>Falha de IA com o que a cadeia de modelos precisa saber (porte do <c>RuntimeError</c> com atributos do Python).</summary>
public class AiException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Status HTTP do provedor, quando houve resposta.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>Queda de rede ou timeout: costuma passar em segundos.</summary>
    public bool Transient { get; init; }

    /// <summary>Preenchido no erro final da cadeia: vale repetir tudo depois de uma pausa?</summary>
    public bool ChainTransient { get; set; }
}

/// <summary>Chave ausente, cota esgotada, autenticação ou provedor fora do ar. A fila repete depois.</summary>
public class AiUnavailableException(string message, Exception? inner = null) : AiException(message, inner);

/// <summary>O modelo respondeu, mas com JSON inválido ou truncado mesmo após as repetições. Também é repetido pela fila.</summary>
public sealed class AiResponseException(string message, Exception? inner = null) : AiUnavailableException(message, inner);
