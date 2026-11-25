using System.Net;
using Dapper;
using System.Text.Json.Nodes;
using Odradek.Core.Actions;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

/// <summary>DDI do telefone: a normalização, a validação do campo e a regra padrão.</summary>
public class PhoneNumberTests
{
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
    public void A_database_without_the_rule_gets_it_on_start()
    {
        using var t = new TempDb();
        Assert.Contains("phone_country_code", t.Db.Open().Query<string>("SELECT key FROM form_field_rules"));
    }
}
