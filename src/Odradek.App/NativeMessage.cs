using System.Runtime.InteropServices;

namespace Odradek.App;

/// <summary>Caixa de mensagem do Windows para avisos antes de existir janela (porta ocupada, falha fatal).</summary>
internal static class NativeMessage
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static void Show(string text) => MessageBox(IntPtr.Zero, text, "Odradek", 0x10); // MB_ICONERROR
}
