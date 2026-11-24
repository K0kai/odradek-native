namespace Odradek.Core.Tests;

/// <summary>
/// O selo "Configurada" das chaves lê o cofre toda vez que a tela atualiza: logo depois de gravar, a chave precisa aparecer. Usa um serviço
/// descartável do Cofre de Credenciais (nunca o <c>job-scraper</c>, que guarda as chaves reais) e o apaga no fim.
/// </summary>
public class SecretStoreTests
{
    [Fact]
    public void A_key_is_visible_right_after_it_is_saved_and_gone_after_it_is_deleted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var service = "odradek-test-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.False(SecretStore.Has("test_secret", service));
            SecretStore.Set("test_secret", "  chave-um  ", service);
            Assert.Equal(("chave-um", SecretStore.Origin.Vault), SecretStore.Lookup("test_secret", service));

            SecretStore.Set("outro_segredo", "chave-dois", service); // o segundo não apaga o primeiro
            Assert.True(SecretStore.Has("test_secret", service));
            Assert.True(SecretStore.Has("outro_segredo", service));

            SecretStore.Set("test_secret", "chave-trocada", service); // trocar o valor vale na hora
            Assert.Equal("chave-trocada", SecretStore.Get("test_secret", service));
        }
        finally
        {
            SecretStore.Delete("test_secret", service);
            SecretStore.Delete("outro_segredo", service);
        }
        Assert.False(SecretStore.Has("test_secret", service));
        Assert.False(SecretStore.Has("outro_segredo", service));
    }
}
