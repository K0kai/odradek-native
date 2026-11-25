using Microsoft.Win32;

namespace Odradek.App;

/// <summary>
/// O registro do instalador (<c>installer\Odradek.iss</c>) em "Adicionar ou remover programas". A atualização automática troca a pasta sem
/// passar pelo instalador, então a versão exibida ali precisa ser corrigida pelo próprio app.
/// </summary>
internal static class InstalledApp
{
    /// <summary>O mesmo <c>AppId</c> do <c>Odradek.iss</c>, com o sufixo que o Inno Setup acrescenta.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8C2154E8-EA11-46CA-A9BA-7C18EF498510}_is1";

    /// <summary>Não faz nada se o app não veio do instalador ou se o registro é de outra pasta. Nunca lança.</summary>
    public static void SetDisplayVersion(string installDir, Version version)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key?.GetValue("InstallLocation") is not string location) return;
            if (!string.Equals(Path.TrimEndingDirectorySeparator(location), installDir, StringComparison.OrdinalIgnoreCase)) return;
            key.SetValue("DisplayVersion", version.ToString(3));
        }
        catch (Exception ex) { Program.Log("Update: não consegui gravar a versão no registro: " + ex.Message); }
    }
}
