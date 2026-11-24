using System.Security.Cryptography;
using System.Text;
using Dapper;
using Odradek.Core.Data;

namespace Odradek.Core.Actions;

/// <summary>Salvar as regras de formulário (<c>web/api_asgi.py:727</c> + <c>jobs/form_rules.py:150</c>): de 1 a 100, chaves únicas.</summary>
public sealed class FormRuleActions(Database db)
{
    public static readonly string[] Modes = ["text", "select", "salary", "file", "cover_letter", "skip"];

    public string Save(IReadOnlyList<PanelQueries.FormRule> items)
    {
        if (items.Count is < 1 or > 100) throw new ActionException("Use de 1 a 100 regras");
        var seen = new HashSet<string>();
        var index = 0;
        foreach (var item in items)
        {
            index++;
            if (string.IsNullOrWhiteSpace(item.Key) || Validate.Length(item.Key) > 100 || !seen.Add(item.Key.ToLowerInvariant()))
                throw new ActionException($"Chave vazia, repetida ou longa na regra {index}");
            var mode = string.IsNullOrEmpty(item.Mode) ? "text" : item.Mode;
            if (!Modes.Contains(mode)) throw new ActionException($"Modo inválido na regra {index}");
            foreach (var (name, value) in new[] { ("key", item.Key), ("aliases", item.Aliases), ("mode", mode), ("value_from", item.ValueFrom), ("value", item.Value) })
                if (Validate.Length(value ?? "") > 10000) throw new ActionException($"Campo {name} inválido na regra {index}");
        }
        using (var connection = db.Open())
        using (var tx = connection.BeginTransaction())
        {
            connection.Execute("DELETE FROM form_field_rules", transaction: tx);
            var order = 10;
            foreach (var item in items)
            {
                var mode = (string.IsNullOrEmpty(item.Mode) ? "text" : item.Mode).Trim().ToLowerInvariant();
                connection.Execute("INSERT INTO form_field_rules(key,aliases,mode,value_from,value,sort_order) VALUES(@key,@aliases,@mode,@valueFrom,@value,@order)",
                    new { key = item.Key.Trim(), aliases = (item.Aliases ?? "").Trim(), mode = Modes.Contains(mode) ? mode : "text", valueFrom = (item.ValueFrom ?? "").Trim(), value = item.Value ?? "", order }, tx);
                order += 10;
            }
            tx.Commit();
        }
        return "Regras de formulário salvas.";
    }
}

/// <summary>Token de pareamento da extensão (<c>web/extension_token.py</c>): só o SHA-256 fica no banco; o token aparece uma vez.</summary>
public sealed class ExtensionTokenActions(Database db)
{
    public const string HashKey = "__extension_token_hash__";
    public const string CreatedKey = "__extension_token_created_at__";

    public static string Digest(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public (bool Configured, string? CreatedAt) Status()
    {
        var store = new SettingsStore(db);
        var created = store.Get(CreatedKey);
        return (store.Get(HashKey).Length > 0, created.Length > 0 ? created : null);
    }

    /// <summary>"odk_" + 32 bytes aleatórios em base64 seguro para URL (43 caracteres), como o <c>secrets.token_urlsafe(32)</c>.</summary>
    public string Issue()
    {
        var token = "odk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        new SettingsStore(db).SetMany(new Dictionary<string, string> { [HashKey] = Digest(token), [CreatedKey] = Clock.NowIso() });
        new EventLogStore(db).Add("info", "extension", "Token de pareamento da extensão gerado.");
        return token;
    }

    public void Revoke()
    {
        new SettingsStore(db).SetMany(new Dictionary<string, string> { [HashKey] = "", [CreatedKey] = "" });
        new EventLogStore(db).Add("info", "extension", "Token de pareamento da extensão revogado.");
    }

    /// <summary>Comparação em tempo constante do token recebido com o hash guardado.</summary>
    public bool Verify(string supplied)
    {
        var stored = new SettingsStore(db).Get(HashKey);
        if (stored.Length == 0 || string.IsNullOrEmpty(supplied)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Digest(supplied)), Encoding.UTF8.GetBytes(stored));
    }
}
