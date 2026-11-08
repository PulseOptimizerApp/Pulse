using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>Pulse's own settings in one place: startup, the admin prompt, updates and language.</summary>
public partial class SettingsPage : UserControl, IPage
{
    public SettingsPage() => InitializeComponent();

    public void OnShown(object parameter) => Build();

    Brush Res(string k) => (Brush)FindResource(k);

    Border Section(string title, params UIElement[] rows)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") });
        foreach (var r in rows) sp.Children.Add(r);
        return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(22, 18, 22, 10), Margin = new Thickness(0, 0, 0, 14), Child = sp };
    }

    /// <summary>A row: label and explanation on the left, a switch on the right.</summary>
    FrameworkElement SwitchRow(string label, string help, bool on, Func<bool, bool> set)
    {
        var g = new Grid { Margin = new Thickness(0, 14, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        if (help != null) left.Children.Add(new TextBlock { Text = Loc.T(help), FontSize = 12, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 24, 0), LineHeight = 17 });
        g.Children.Add(left);
        var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        sw.Click += (_, _) => { if (!set(sw.IsChecked == true)) sw.IsChecked = !(sw.IsChecked == true); };
        Grid.SetColumn(sw, 1);
        g.Children.Add(sw);
        return g;
    }

    void Build()
    {
        Root.Children.Clear();
        var st = TweakEngine.State.Settings;

        var tray = SwitchRow("Start in the tray", "The window stays closed until you open it from the tray icon.", st.StartInTray,
            v => { st.StartInTray = v; TweakEngine.Save(); return true; });
        tray.Visibility = AppStartup.IsOn ? Visibility.Visible : Visibility.Collapsed;
        Root.Children.Add(Section("Startup",
            SwitchRow("Start Pulse with Windows", "Pulse opens a few seconds after you sign in, so your clips, macros, crosshair, GPU tune and match recaps are ready before you start a game.", AppStartup.IsOn,
                v =>
                {
                    if (!AppStartup.Set(v) && v) { MainWindow.Instance.Toast(Loc.T("Windows didn't allow it."), ToastKind.Warning); return false; }
                    tray.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
                    return true;
                }),
            tray,
            SwitchRow("Open without the admin prompt", "Pulse needs admin rights to change system settings, so Windows asks each time you open it. With this on, Pulse opens with them straight away through a Windows Task Scheduler entry.", Elevation.IsOn(),
                v =>
                {
                    if (v && !Elevation.Enable()) { MainWindow.Instance.Toast(Loc.T("Windows didn't allow it. Pulse will keep asking."), ToastKind.Warning); return false; }
                    if (!v) Elevation.Disable();
                    TweakEngine.AddLog("app", v ? "Opens without the admin prompt" : "Asks for admin rights again");
                    return true;
                })));

        var check = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Check now"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 4, 0, 10) };
        check.Click += (_, _) => MainWindow.Instance.CheckForUpdatesNow();
        Root.Children.Add(Section("Updates",
            SwitchRow("Check for updates automatically", "Pulse looks for a newer version on its GitHub page when it starts. Nothing is installed without asking.", st.CheckForUpdates,
                v => { st.CheckForUpdates = v; TweakEngine.Save(); return true; }),
            new TextBlock { Text = Loc.F("You have Pulse {0}.", Updater.CurrentVersion), FontSize = 12, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 4, 0, 8) },
            check));

        var langs = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        var choice = st.Language ?? "auto";
        var options = new List<(string Code, string Name)> { ("auto", Loc.T("Match Windows")) };
        options.AddRange(Loc.Languages);
        foreach (var (code, name) in options)
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), Content = name, GroupName = "lang", IsChecked = code == choice, Margin = new Thickness(0, 0, 6, 6) };
            var c = code;
            chip.Checked += (_, _) => MainWindow.Instance.SetLanguage(c);
            langs.Children.Add(chip);
        }
        Root.Children.Add(Section("Language", langs));

        var move = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Open Undo & restore"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 12, 0, 10) };
        move.Click += (_, _) => MainWindow.Instance.Navigate("safety");
        Root.Children.Add(Section("Backup and undo",
            new TextBlock { Text = Loc.T("Move your settings to another PC, undo tweaks, and make restore points from Undo & restore."), FontSize = 12, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) },
            move));
    }
}
