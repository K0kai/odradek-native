namespace Odradek.Core.Extension;

/// <summary>
/// A extensão "Odradek Autofill" (<c>extension\</c>). A chave pública em <c>manifest.json</c> ("key") fixa o ID em qualquer PC e pasta; sem
/// ela o navegador derivaria o ID do caminho. É por esse ID que o app reconhece a extensão quando ela pede para se conectar.
/// </summary>
public static class ExtensionIdentity
{
    public const string Id = "hddkpodipkbnkicbobebhnaabdailkfg";

    /// <summary>O cabeçalho <c>Origin</c> dos pedidos da extensão. Uma página não consegue forjá-lo.</summary>
    public const string Origin = "chrome-extension://" + Id;
}
