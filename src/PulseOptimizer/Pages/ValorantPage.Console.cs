using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core;
using Pulse.Core.Crosshair;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>For playing VALORANT on console too: your crosshair to carry over, and the settings worth matching.</summary>
public partial class ValorantPage
{
    static ConsoleSwitchSettings ConsoleSet => TweakEngine.State.ConsoleSwitch ??= new ConsoleSwitchSettings();

    void BuildConsole()
    {
        Root.Children.Add(ConsoleCrosshairCard());
        Root.Children.Add(MatchSettingsCard());
    }

    FrameworkElement ConsoleCrosshairCard()
    {
        var s = ConsoleSet;
        var sp = new StackPanel();
        sp.Children.Add(Heading("Your crosshair on console"));
        sp.Children.Add(Dim(Loc.T("Console VALORANT can import crosshair codes too. On PC, go to Settings › Crosshair and press Export, then paste the code here. Pulse shows it in short pieces that are easy to type."), 12.5, 4));
        var box = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = "0;P;c;5;…", Text = s.CrosshairCode, Margin = new Thickness(0, 12, 0, 0), FontSize = 13 };
        var shown = new StackPanel();
        void Show()
        {
            shown.Children.Clear();
            var code = (s.CrosshairCode ?? "").Trim();
            if (code.Length == 0) return;
            if (ValorantCode.Parse(code) == null) { shown.Children.Add(Faint(Loc.T("That isn't a VALORANT crosshair code. It starts with 0;P;"), 11.5, 8)); return; }
            var pieces = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            for (int i = 0; i < code.Length; i += 8)
                pieces.Children.Add(new Border { Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(8), Background = Res("SurfaceHoverBrush"),
                    Child = new TextBlock { Text = code.Substring(i, Math.Min(8, code.Length - i)), FontSize = 18, FontFamily = new FontFamily("Consolas"), Foreground = Res("TextBrush") } });
            shown.Children.Add(pieces);
            var copy = Btn("Copy");
            copy.HorizontalAlignment = HorizontalAlignment.Left;
            copy.Click += (_, _) => { try { Clipboard.SetText(code); MainWindow.Instance.Toast(Loc.T("Copied."), ToastKind.Success); } catch { } };
            shown.Children.Add(copy);
            shown.Children.Add(Faint(Loc.T("Typing with a controller is slow: plug a USB keyboard into the console and type it in a few seconds."), 11.5, 10));
        }
        box.TextChanged += (_, _) => { s.CrosshairCode = box.Text.Trim(); TweakEngine.Save(); Show(); };
        sp.Children.Add(box);
        sp.Children.Add(shown);
        Show();
        return Card(sp);
    }

    FrameworkElement MatchSettingsCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Keep these the same on both"));
        sp.Children.Add(Dim(Loc.T("Not every setting carries over between PC and console. Check these on both so switching feels the same:"), 12.5, 4));
        foreach (var (what, why) in new[]
        {
            ("Crosshair", "The code above."),
            ("Enemy highlight color", "So enemies pop out the same way."),
            ("Minimap: rotate, keep player centered, size and zoom", "So callouts and positions read the same."),
            ("Brightness and display mode", "So dark corners look the same."),
            ("Voice chat and push-to-talk", "So your team hears you the same way."),
        })
        {
            var r = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            r.Children.Add(new TextBlock { Text = "✓  " + Loc.T(what), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            r.Children.Add(Faint(Loc.T(why), 11.5, 2).Also(f => f.Margin = new Thickness(20, 2, 0, 0)));
            sp.Children.Add(r);
        }
        sp.Children.Add(Faint(Loc.T("Aim settings don't carry from mouse to controller: they work differently, so set those on console by feel."), 11.5, 12));
        return Card(sp);
    }
}
