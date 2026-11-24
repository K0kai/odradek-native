namespace Odradek.Core;

/// <summary>Pastas do app. Mesmas regras do <c>backend/paths.py</c> no app empacotado.</summary>
public static class Paths
{
    /// <summary>Dados do usuário: <c>ODRADEK_DATA_DIR</c>, senão <c>%LOCALAPPDATA%\Odradek</c> (onde o app Python empacotado grava).</summary>
    public static string DataDir { get; } = ResolveDataDir();

    public static string DbPath => Path.Combine(DataDir, "jobs.db");
    public static string LogPath => Path.Combine(DataDir, "desktop.log");
    public static string ResumesDir => Environment.GetEnvironmentVariable("RESUMES_DIR") is { Length: > 0 } custom
        ? custom : Path.Combine(DataDir, "resumes");

    private static string ResolveDataDir()
    {
        var overrideDir = (Environment.GetEnvironmentVariable("ODRADEK_DATA_DIR") ?? "").Trim();
        if (overrideDir.Length > 0) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideDir));
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        return Path.Combine(local, "Odradek");
    }
}
