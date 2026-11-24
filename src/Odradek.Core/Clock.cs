using System.Globalization;

namespace Odradek.Core;

/// <summary>Datas como o Python grava (<c>datetime.now(timezone.utc).isoformat(timespec="seconds")</c>): o SQL compara texto.</summary>
public static class Clock
{
    public static string NowIso() => ToIso(DateTime.UtcNow);

    public static string ToIso(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);

    /// <summary>Lê as strings do banco (com <c>+00:00</c>, <c>Z</c> ou sem fuso, que conta como UTC).</summary>
    public static bool TryParse(string? text, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) return false;
        utc = parsed.UtcDateTime;
        return true;
    }
}
