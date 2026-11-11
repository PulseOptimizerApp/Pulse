using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core;
using Pulse.Core.Display;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>Brighter, punchier colours in VALORANT through the graphics card's own vibrance setting.</summary>
public partial class ValorantPage
{
    static VibranceSettings VibranceSet => TweakEngine.State.Vibrance ??= new VibranceSettings();

    void BuildVibrance()
    {
        var s = VibranceSet;
        var card = VibranceService.Card;
        var sp = new StackPanel();
        sp.Children.Add(Heading("Make VALORANT more vibrant"));
        sp.Children.Add(Dim(Loc.T("Richer colours make enemies and their outlines pop. Pulse turns up your graphics card's own vibrance only while VALORANT is in front, and puts it back to normal when you leave the game."), 12.5, 4));

        if (card == null)
        {
            sp.Children.Add(Dim(Loc.T("Pulse couldn't find a vibrance control on this PC. It works with NVIDIA (Digital Vibrance) and AMD (Saturation) graphics cards. On Intel, use Saturation in Intel Graphics Software."), 12.5, 12));
            Root.Children.Add(Card(sp));
            return;
        }

        var on = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = s.Enabled, Content = Loc.T("More vibrant colours in VALORANT"), Margin = new Thickness(0, 16, 0, 0), Foreground = Res("TextBrush") };
        on.Checked += (_, _) => { s.Enabled = true; TweakEngine.Save(); VibranceService.Refresh(); };
        on.Unchecked += (_, _) => { s.Enabled = false; TweakEngine.Save(); VibranceService.Refresh(); };
        sp.Children.Add(on);

        // Strength.
        var label = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 18, 0, 6) };
        string Describe(int v) => v <= 55 ? Loc.T("Normal") : v <= 70 ? Loc.T("A little more") : v <= 85 ? Loc.T("Vivid (most players' pick)") : Loc.T("Maximum");
        void Show(int v) => label.Text = Loc.F("Strength: {0}% · {1}", v, Describe(v));
        Show(s.Level);
        sp.Children.Add(label);
        var slider = new Slider { Minimum = 50, Maximum = 100, Value = Math.Max(50, s.Level), TickFrequency = 5, IsSnapToTickEnabled = true, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, Width = 420 };
        slider.ValueChanged += (_, e) => { s.Level = (int)e.NewValue; Show(s.Level); TweakEngine.Save(); VibranceService.Refresh(); };
        sp.Children.Add(slider);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var preview = Btn("Preview for 3 seconds");
        preview.Click += async (_, _) => { preview.IsEnabled = false; await VibranceService.Preview(s.Level); preview.IsEnabled = true; };
        row.Children.Add(preview);
        sp.Children.Add(row);

        sp.Children.Add(Faint(Loc.F("Uses your {0} graphics card's own setting, the same slider as its control panel. Nothing in the game is changed.", card.Card), 11.5, 14));
        sp.Children.Add(Faint(Loc.T("Tip: about 75% is what most players use. Higher makes reds and yellows very strong."), 11.5, 4));
        Root.Children.Add(Card(sp));
    }
}
