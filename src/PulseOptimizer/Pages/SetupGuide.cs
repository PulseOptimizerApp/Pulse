using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The "How to set it up" guide on the Mic studio and Headphones pages: numbered steps for virtual cable mode and
/// no-cable mode, with buttons for the downloads and settings, and fixes for common problems. Folds away.
/// </summary>
static class SetupGuide
{
    public record Step(string Text, string Button = null, string Url = null);
    public record Way(string Key, string Title, Step[] Steps, string[] Fixes);

    static readonly Dictionary<string, bool> Open = new();
    static readonly Dictionary<string, string> Chosen = new();

    public static Way[] Mic => new[]
    {
        new Way("cable", "Virtual cable", new[]
        {
            new Step("Install VB-CABLE (free): download it, unzip it, right-click VBCABLE_Setup_x64 and choose Run as administrator, then Install Driver. Restart your PC.", "Get VB-CABLE", "https://vb-audio.com/Cable/"),
            new Step("In Pulse: pick Virtual cable at the top of this page, choose your real mic, and turn the Mic studio on. It shows \"Sending voice\" when you talk."),
            new Step("Discord: Settings → Voice & Video → Input Device: \"CABLE Output (VB-Audio Virtual Cable)\". Turn off Discord's Noise Suppression, since Pulse already does it."),
            new Step("Games: in the game's voice chat settings, pick \"CABLE Output\" as the microphone. If a game has no mic setting, make CABLE Output the default in Windows sound settings.", "Open sound settings", "ms-settings:sound"),
            new Step("OBS: add an Audio Input Capture source with \"CABLE Output\" and mute your normal mic there."),
            new Step("Test it: the Sound check tab plays your voice before and after, or use Discord's \"Let's Check\"."),
        }, new[]
        {
            "Discord hears nothing: check the Mic studio is on and the level bar moves when you talk, and that Discord's input is CABLE Output (not CABLE In).",
            "You hear yourself in your headset: turn off \"Listen to yourself\" here, and \"Listen to this device\" in Windows' mic properties.",
            "Your voice sounds robotic or cuts out: set the noise removal to Light, or turn off Discord's own noise suppression and echo cancellation.",
            "Pulse says there's no cable: restart your PC after installing VB-CABLE, then press Check again.",
        }),
        new Way("apo", "No cable (Equalizer APO)", new[]
        {
            new Step("Install Equalizer APO (free) and run its installer.", "Get Equalizer APO", "https://sourceforge.net/projects/equalizerapo/"),
            new Step("When its Configurator opens, go to Capture devices and tick your real mic. Restart your PC."),
            new Step("In Pulse: pick No cable at the top of this page, choose your mic, and turn the Mic studio on."),
            new Step("Discord and games: keep your normal mic selected. Nothing else to change."),
            new Step("Test it with Discord's \"Let's Check\" while you type: the keyboard noise should be gone."),
        }, new[]
        {
            "No change in sound: open Equalizer APO's Configurator, check your mic is ticked under Capture devices, and restart.",
            "Voice effects and the soundboard need the Virtual cable way.",
            "After a big Windows or driver update, if it stops working, open the Configurator once and press OK.",
        }),
    };

    public static Way[] Headphones => new[]
    {
        new Way("cable", "Virtual cable", new[]
        {
            new Step("Install Hi-Fi Cable (free) from VB-Audio, a second cable just for game sound (VB-CABLE stays for your mic). Run its installer as administrator and restart your PC.", "Get Hi-Fi Cable", "https://vb-audio.com/Cable/"),
            new Step("In Pulse: pick Virtual cable at the top of this page. Game sound comes in through \"Hi-Fi Cable Output\" and plays in your headset. Turn Game audio on."),
            new Step("Send the game into the cable: start the game, open Windows sound settings → Volume mixer, find the game and set its Output device to \"Hi-Fi Cable Input\". Some games have this in their own audio settings.", "Open the volume mixer", "ms-settings:apps-volume"),
            new Step("Keep Discord and music on your headset directly, so only the game gets the footsteps EQ."),
            new Step("Pick a preset, then hold \"Hold to hear without Pulse\" to compare."),
        }, new[]
        {
            "No game sound at all: check Game audio is on and the level bar moves, and that the game's output is Hi-Fi Cable Input.",
            "Sound is doubled or echoey: the game is playing to both your headset and the cable. Set only its output to the cable.",
            "Delay feels off: use the No cable way instead (no added delay).",
            "Turn off Windows Sonic, Dolby Atmos or DTS on your headset so the sound isn't processed twice.",
        }),
        new Way("apo", "No cable (Equalizer APO)", new[]
        {
            new Step("Install Equalizer APO (free).", "Get Equalizer APO", "https://sourceforge.net/projects/equalizerapo/"),
            new Step("In its Configurator, go to Playback devices and tick your headset. Restart your PC."),
            new Step("In Pulse: pick No cable at the top of this page, choose your headset, and turn Game audio on."),
            new Step("Games play to your headset as usual: the preset applies to everything you hear on it, with no added delay."),
        }, new[]
        {
            "No change in sound: open Equalizer APO's Configurator and check your headset is ticked under Playback devices, then restart.",
            "Even out loud and quiet needs the Virtual cable way.",
        }),
    };

    /// <summary>The guide as a card. <paramref name="current"/> is the way in use, shown first.</summary>
    public static FrameworkElement Build(FrameworkElement owner, string id, Way[] ways, string current, bool startOpen)
    {
        Brush Res(string k) => (Brush)owner.FindResource(k);
        if (!Open.ContainsKey(id)) Open[id] = startOpen;
        var chosen = Chosen.TryGetValue(id, out var c) ? c : current ?? ways[0].Key;
        var way = ways.FirstOrDefault(w => w.Key == chosen) ?? ways[0];

        var root = new StackPanel();
        var card = new Border { Style = (Style)owner.FindResource("Card"), Padding = new Thickness(20, 16, 20, 16), Margin = new Thickness(0, 0, 0, 16), Child = root };
        var head = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var arrow = new TextBlock { Text = Open[id] ? "▾" : "▸", FontSize = 13, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(arrow, Dock.Right);
        head.Children.Add(arrow);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new LineIcon { Kind = "guide", Width = 16, Height = 16, Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 10, 0) });
        title.Children.Add(new TextBlock { Text = Loc.T("How to set it up"), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        head.Children.Add(title);
        root.Children.Add(head);

        var body = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = Open[id] ? Visibility.Visible : Visibility.Collapsed };
        head.MouseLeftButtonUp += (_, _) =>
        {
            Open[id] = !Open[id];
            body.Visibility = Open[id] ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = Open[id] ? "▾" : "▸";
        };

        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var stepsHost = new StackPanel();
        foreach (var w in ways)
        {
            var rb = new RadioButton { Style = (Style)owner.FindResource("Chip"), Content = Loc.T(w.Title), GroupName = "guide-" + id, IsChecked = w == way, Margin = new Thickness(0, 0, 6, 0) };
            rb.Checked += (_, _) => { Chosen[id] = w.Key; Fill(w); };
            tabs.Children.Add(rb);
        }
        body.Children.Add(tabs);
        body.Children.Add(stepsHost);
        root.Children.Add(body);

        void Fill(Way w)
        {
            stepsHost.Children.Clear();
            for (int i = 0; i < w.Steps.Length; i++)
            {
                var s = w.Steps[i];
                var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Res("Surface2Brush"), VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
                var col = new StackPanel();
                col.Children.Add(new TextBlock { Text = Loc.T(s.Text), FontSize = 13, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                if (s.Button != null)
                {
                    var b = new Button { Style = (Style)owner.FindResource("GhostButton"), Content = Loc.T(s.Button), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
                    var url = s.Url;
                    b.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
                    col.Children.Add(b);
                }
                Grid.SetColumn(col, 1);
                row.Children.Add(col);
                stepsHost.Children.Add(row);
            }
            stepsHost.Children.Add(new TextBlock { Text = Loc.T("If something's off"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 6, 0, 6) });
            foreach (var f in w.Fixes)
                stepsHost.Children.Add(new TextBlock { Text = "•  " + Loc.T(f), FontSize = 12, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        }
        Fill(way);
        return card;
    }
}
