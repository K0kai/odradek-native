using Odradek.Core;

public static class SecretsProbe
{
    private const string TestName = "odradek_probe_test";
    private const string TestService = "odradek-probe-test"; // serviço descartável: não toca nas credenciais reais

    public static int Run(string[] args)
    {
        if (args.Length >= 1 && args[0] == "write") { SecretStore.Set(TestName, "valor-de-teste-ç-😀", TestService); Console.WriteLine("gravado"); return 0; }
        if (args.Length >= 1 && args[0] == "read") { Console.WriteLine(SecretStore.Get(TestName, TestService) == "valor-de-teste-ç-😀" ? "igual" : "DIFERENTE"); return 0; }
        if (args.Length >= 1 && args[0] == "delete") { Console.WriteLine(SecretStore.Delete(TestName, TestService) ? "removido" : "não existia"); return 0; }

        foreach (var name in new[] { "gemini", "apify_token", "tokenharbor", "openai", "adzuna_app_id", "adzuna_app_key", "smtp_password", "qpid_api_token" })
        {
            var (value, origin) = SecretStore.Lookup(name);
            Console.WriteLine($"{name,-16} {(value.Length > 0 ? $"presente ({value.Length} caracteres, origem {origin})" : "ausente")}");
        }
        return 0;
    }
}
