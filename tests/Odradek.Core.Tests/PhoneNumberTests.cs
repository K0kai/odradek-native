using System.Net;
using Dapper;
using System.Text.Json.Nodes;
using Odradek.Core.Actions;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

/// <summary>Telefone com DDI separado: a divisão, a validação do campo e o que a extensão recebe.</summary>
public class PhoneNumberTests
{
    [Theory]
    // sem + : vale o DDI configurado e o número é nacional
    [InlineData("(11) 99999-0000", "55", "55", "11999990000")]
    [InlineData("11 99999-0000", "+55", "55", "11999990000")]
    [InlineData("912 345 678", "351", "351", "912345678")]
    [InlineData("5511999990000", "55", "55", "11999990000")]   // 55 + 11 dígitos: já vem completo
    [InlineData("55 99999-9999", "55", "55", "55999999999")]   // DDD 55 (RS), 11 dígitos: nacional, não é o DDI
    // com + ou 00 : o número traz o próprio DDI e manda sobre o configurado
    [InlineData("+55 11 99999-0000", "55", "55", "11999990000")]
    [InlineData("+351 912 345 678", "55", "351", "912345678")]
    [InlineData("0044 7700 900123", "55", "44", "7700900123")]
    [InlineData("+1 415 555 0100", "", "1", "4155550100")]
    // sem DDI configurado e sem +: só o número (quem preenche decide)
    [InlineData("11 99999-0000", "", "", "11999990000")]
    // vazio
    [InlineData("", "55", "", "")]
    [InlineData("sem número", "55", "", "")]
    public void Split_separates_the_country_code_from_the_national_number(string raw, string configured, string code, string national) =>
        Assert.Equal((code, national), PhoneNumber.Split(raw, configured));

    [Fact]
    public void An_invalid_configured_code_is_ignored_when_splitting() =>
        Assert.Equal(("", "11999990000"), PhoneNumber.Split("11 99999-0000", "abc"));

    [Theory]
    [InlineData("55", "55")] [InlineData("+55", "55")] [InlineData(" 0055 ", "55")] [InlineData("+ 351", "351")] [InlineData("1", "1")]
    [InlineData("", "")] [InlineData("abc", "")] [InlineData("5a", "")]
    public void NormalizeCountryCode_keeps_only_the_digits(string raw, string expected) => Assert.Equal(expected, PhoneNumber.NormalizeCountryCode(raw));

    [Theory]
    [InlineData("1", true)] [InlineData("55", true)] [InlineData("351", true)]
    [InlineData("", false)] [InlineData("0", false)] [InlineData("0055", false)] [InlineData("1234", false)]
    public void IsValidCountryCode_takes_one_to_three_digits_without_a_leading_zero(string digits, bool expected) =>
        Assert.Equal(expected, PhoneNumber.IsValidCountryCode(digits));

    [Fact]
    public void The_profile_form_saves_only_the_digits_and_refuses_a_bad_code()
    {
        using var t = new TempDb();
        var actions = new SettingsActions(t.Db);
        Assert.Equal("55", new SettingsStore(t.Db).Get("candidate_phone_country_code")); // padrão: Brasil
        actions.Save(new Dictionary<string, string> { ["candidate_phone_country_code"] = " +351 " });
        Assert.Equal("351", new SettingsStore(t.Db).Get("candidate_phone_country_code"));
        actions.Save(new Dictionary<string, string> { ["candidate_phone_country_code"] = "" }); // vazio é permitido (desliga o DDI)
        Assert.Equal("", new SettingsStore(t.Db).Get("candidate_phone_country_code"));
        foreach (var bad in new[] { "abc", "0", "1234", "+" })
            Assert.StartsWith("Código do país inválido", Assert.Throws<ActionException>(() => actions.Save(new Dictionary<string, string> { ["candidate_phone_country_code"] = bad })).Message);
    }

    [Fact]
    public void A_database_without_the_rule_gets_it_on_start_and_the_extension_receives_it()
    {
        using var t = new TempDb();
        Assert.Contains("phone_country_code", t.Db.Open().Query<string>("SELECT key FROM form_field_rules"));
    }

    private static string IssueToken(ApiHarness api) => new ExtensionTokenActions(api.Temp.Db).Issue();

    [Fact]
    public async Task Autofill_profile_sends_the_phone_whole_and_split()
    {
        using var api = ApiHarness.Start();
        var settings = new SettingsStore(api.Temp.Db);
        var token = IssueToken(api);

        settings.SetMany(new Dictionary<string, string> { ["candidate_phone"] = "(11) 99999-0000", ["candidate_phone_country_code"] = "55" });
        var (_, profile) = await api.Json("GET", "/api/autofill-profile", token);
        var p = profile!["profile"]!;
        Assert.Equal(("+5511999990000", "55", "11999990000"), (p["phone"]!.GetValue<string>(), p["phoneCountryCode"]!.GetValue<string>(), p["phoneNational"]!.GetValue<string>()));

        settings.SetMany(new Dictionary<string, string> { ["candidate_phone"] = "+351 912 345 678" }); // o + manda sobre o DDI configurado
        p = (await api.Json("GET", "/api/autofill-profile", token)).Item2!["profile"]!;
        Assert.Equal(("+351912345678", "351", "912345678"), (p["phone"]!.GetValue<string>(), p["phoneCountryCode"]!.GetValue<string>(), p["phoneNational"]!.GetValue<string>()));

        settings.SetMany(new Dictionary<string, string> { ["candidate_phone"] = "11 99999-0000", ["candidate_phone_country_code"] = "" }); // sem DDI: como antes
        p = (await api.Json("GET", "/api/autofill-profile", token)).Item2!["profile"]!;
        Assert.Equal(("11 99999-0000", "", "11999990000"), (p["phone"]!.GetValue<string>(), p["phoneCountryCode"]!.GetValue<string>(), p["phoneNational"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Autofill_rules_include_the_country_code_field()
    {
        using var api = ApiHarness.Start();
        var (status, rules) = await api.Json("GET", "/api/autofill-rules", IssueToken(api));
        Assert.Equal(HttpStatusCode.OK, status);
        var field = rules!["fields"]!.AsArray().Single(f => f!["key"]!.GetValue<string>() == "phoneCountryCode")!;
        Assert.Contains("ddi", field["aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Contains("country code", field["aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
    }
}
