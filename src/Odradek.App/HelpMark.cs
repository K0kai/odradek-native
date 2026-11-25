using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Odradek.App;

/// <summary>O marcador (?): a explicação aparece ao passar o mouse ou ao clicar (também pelo teclado).</summary>
public sealed class HelpMark : Button
{
    private string _text = "";

    public HelpMark()
    {
        Content = "?";
        Classes.Add("help");
        VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetShowDelay(this, 150);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            Avalonia.Automation.AutomationProperties.SetName(this, "Ajuda: " + value);
            ToolTip.SetTip(this, Body(value));
            var flyout = new Flyout { Content = Body(value) };
            flyout.Opened += (_, _) => ToolTip.SetIsOpen(this, false); // a dica e o balão não ficam um por cima do outro
            Flyout = flyout;
        }
    }

    private static TextBlock Body(string text) => new() { Text = text, MaxWidth = 300, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = FontWeight.Normal };
}
