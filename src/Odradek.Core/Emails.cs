using System.Text.RegularExpressions;

namespace Odradek.Core;

/// <summary>E-mails de contato de uma vaga (<c>jobs/apply_channels.py:11-33</c>).</summary>
public static partial class Emails
{
    private static readonly string[] SkipLocal = ["noreply", "no-reply", "donotreply", "do-not-reply", "mailer-daemon", "notifications"];

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")]
    private static partial Regex EmailRegex();

    public static List<string> Extract(string? text)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in EmailRegex().Matches(text ?? ""))
        {
            var address = match.Value;
            var local = address[..address.IndexOf('@')].ToLowerInvariant();
            if (SkipLocal.Any(skip => local.Contains(skip, StringComparison.Ordinal))) continue;
            if (!seen.Add(address.ToLowerInvariant())) continue;
            found.Add(address);
        }
        return found;
    }

    /// <summary>Endereços que aparecem na descrição, na URL ou no nome da empresa.</summary>
    public static List<string> JobContacts(string? description, string? url, string? company) =>
        Extract(string.Join(' ', description ?? "", url ?? "", company ?? ""));
}
