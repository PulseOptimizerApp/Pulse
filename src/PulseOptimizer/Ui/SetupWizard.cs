using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Pulse.Core;
using Pulse.Core.Games;
using Pulse.Core.Games.Brawlhalla;
using Pulse.Pages;

namespace Pulse.Ui;

/// <summary>
/// A two-minute first-run setup over the whole window: pick your games, check the PC, apply the recommended tweaks,
/// tell Pulse who you are (Brawlhalla; VALORANT finds you on its own), and where to go next. Every step can be skipped;
/// nothing is changed without a click. Opens once on a fresh install, and again from Settings.
/// </summary>
public sealed class SetupWizard : Grid
{
    static SetupState S => TweakEngine.State.Setup ??= new SetupState();

    readonly List<string> _steps = new();
    int _step;
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _title = new(), _sub = new();
    readonly ContentControl _body = new();
    readonly Button _back, _next;
    readonly Button _skip;

    // Step state.
    List<GameItem> _games;
    readonly HashSet<string> _picked = new();
    bool _showAllGames;
    List<Finding> _findings;
    bool _checking;
    List<Tweak> _tweaks;
    readonly HashSet<string> _tweakPicked = new();
    bool _applying;
    int _applied = -1;
    string _bhStatus;
    List<BhRanking> _bhResults;
    bool _bhBusy;

    public event Action Closed;

    public SetupWizard()
    {
        Background = (Brush)Application.Current.FindResource("BgBrush");
        var card = new Grid { MaxWidth = 860, Margin = new Thickness(40, 24, 40, 28) };
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition());
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        card.Children.Add(_dots);
        var head = new StackPanel { Margin = new Thickness(0, 22, 0, 18) };
        _title.FontSize = 26; _title.FontWeight = FontWeights.Bold; _title.Foreground = Res("TextBrush"); _title.FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont");
        _title.TextWrapping = TextWrapping.Wrap;
        _sub.FontSize = 14; _sub.Foreground = Res("TextDimBrush"); _sub.TextWrapping = TextWrapping.Wrap; _sub.Margin = new Thickness(0, 6, 0, 0); _sub.LineHeight = 21;
        head.Children.Add(_title);
        head.Children.Add(_sub);
        Grid.SetRow(head, 1);
        card.Children.Add(head);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _body };
        Grid.SetRow(scroll, 2);
        card.Children.Add(scroll);

        var foot = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        _skip = new Button { Style = (Style)Application.Current.FindResource("LinkButton"), Content = Loc.T("Skip setup"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
        _skip.Click += (_, _) => Finish();
        foot.Children.Add(_skip);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _back = new Button { Style = (Style)Application.Current.FindResource("GhostButton"), Content = Loc.T("Back"), Padding = new Thickness(18, 8, 18, 9), Margin = new Thickness(0, 0, 10, 0) };
        _back.Click += (_, _) => Go(_step - 1);
        _next = new Button { Style = (Style)Application.Current.FindResource("PrimaryButton"), Padding = new Thickness(22, 8, 22, 9) };
        _next.Click += (_, _) => Next();
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        foot.Children.Add(buttons);
        Grid.SetRow(foot, 3);
        card.Children.Add(foot);
        Children.Add(card);

        foreach (var id in S.Games) _picked.Add(id);
        _ = LoadGames();
        Steps();
        Go(0);
    }

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>The steps: "You" only when a picked game has an account step.</summary>
    void Steps()
    {
        var cur = _steps.Count > 0 ? _steps[_step] : null;
        _steps.Clear();
        _steps.AddRange(new[] { "welcome", "games", "check", "tweaks" });
        if (_picked.Contains("brawlhalla") || _picked.Contains("valorant")) _steps.Add("you");
        _steps.Add("done");
        if (cur != null) _step = Math.Max(0, _steps.IndexOf(cur));
    }

    async Task LoadGames()
    {
        try { _games = await LatencyPlan.GamesAsync(); } catch { _games = new(); }
        // The first time, the games on this PC start picked.
        if (S.Games.Count == 0)
            foreach (var g in _games.Where(g => g.Installed && !g.Game.IsAddTile && g.Game.Id != "other" && !g.Game.IsCustom)) _picked.Add(g.Game.Id);
        Steps();
        if (_steps[_step] == "games") Render();
    }

    void Next()
    {
        switch (_steps[_step])
        {
            case "games":
                S.Games = _picked.ToList();
                TweakEngine.Save();
                Steps();
                break;
            case "done":
                Finish();
                return;
        }
        Go(_step + 1);
    }

    void Go(int step)
    {
        _step = Math.Clamp(step, 0, _steps.Count - 1);
        Render();
        var tt = new TranslateTransform(0, 8);
        _body.RenderTransform = tt;
        _body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void Render()
    {
        var key = _steps[_step];
        // Progress dots: one per real step (not the welcome and done screens).
        _dots.Children.Clear();
        var real = _steps.Where(s => s is not ("welcome" or "done")).ToList();
        for (int i = 0; i < real.Count; i++)
        {
            int at = _steps.IndexOf(real[i]);
            bool on = at == _step, past = at < _step;
            var label = real[i] switch { "games" => "Your games", "check" => "Check your PC", "tweaks" => "Tweaks", _ => "You" };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
            sp.Children.Add(new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 8, 0),
                Background = on ? Res("AccentBrush") : past ? Res("GoodSoftBrush") : Res("SurfaceBrush"),
                Child = new TextBlock { Text = past ? "✓" : (i + 1).ToString(CultureInfo.CurrentCulture), FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = on ? Res("OnAccentBrush") : past ? Res("GoodBrush") : Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 12.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = on ? Res("TextBrush") : Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center });
            _dots.Children.Add(sp);
        }
        _dots.Visibility = key is "welcome" or "done" ? Visibility.Hidden : Visibility.Visible;

        _back.Visibility = _step > 0 && key != "done" ? Visibility.Visible : Visibility.Collapsed;
        _skip.Visibility = key == "done" ? Visibility.Collapsed : Visibility.Visible;
        _next.IsEnabled = !_checking && !_applying;
        switch (key)
        {
            case "welcome": Welcome(); break;
            case "games": Games(); break;
            case "check": Check(); break;
            case "tweaks": Tweaks(); break;
            case "you": You(); break;
            default: Done(); break;
        }
    }

    void Head(string title, string sub, string next)
    {
        _title.Text = Loc.T(title);
        _sub.Text = sub == null ? "" : Loc.T(sub);
        _sub.Visibility = sub == null ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = Loc.T(next);
    }

    // ───────── welcome ─────────

    void Welcome()
    {
        Head("Welcome to Pulse", "Two minutes to set Pulse up for your PC and your games. Every step can be skipped, and nothing changes without your click.", "Let's go");
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (n, title, text) in new[]
        {
            ("1", "Pick your games", "Pulse finds the games on this PC. Tell it which ones you play."),
            ("2", "Check your PC", "A one-minute scan for what's holding it back: memory speed, drivers, power settings and more."),
            ("3", "Apply the safe tweaks", "Only the documented ones that fit this PC, each backed up and undoable."),
            ("4", "Tell Pulse who you are", "So your stats and matches open straight away."),
        })
            sp.Children.Add(Row(n, title, text));
        _body.Content = sp;
    }

    FrameworkElement Row(string n, string title, string text)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Res("SurfaceBrush"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = n, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        t.Children.Add(new TextBlock { Text = Loc.T(text), FontSize = 13, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    // ───────── games ─────────

    void Games()
    {
        Head("Which games do you play?", "The ones on this PC are picked already. Pulse uses this to point you to the right settings, trackers and fixes.", "Next");
        var sp = new StackPanel();
        if (_games == null) { sp.Children.Add(Dim(Loc.T("Looking for games on this PC…"))); _body.Content = sp; return; }
        var known = _games.Where(g => !g.Game.IsAddTile && g.Game.Id != "other" && !g.Game.IsCustom).ToList();
        var installed = known.Where(g => g.Installed).ToList();
        var rest = known.Where(g => !g.Installed).OrderBy(g => g.Game.Name).ToList();
        if (installed.Count > 0)
        {
            sp.Children.Add(Caption(Loc.F("On this PC ({0})", installed.Count)));
            sp.Children.Add(Tiles(installed));
        }
        else sp.Children.Add(Dim(Loc.T("Pulse didn't find any games it knows on this PC. Pick the ones you play below.")));
        var more = new Button { Style = (Style)Application.Current.FindResource("LinkButton"), Content = _showAllGames ? Loc.T("Hide other games") : Loc.F("Show {0} more games", rest.Count), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 6) };
        more.Click += (_, _) => { _showAllGames = !_showAllGames; Render(); };
        if (rest.Count > 0) sp.Children.Add(more);
        if (_showAllGames) sp.Children.Add(Tiles(rest));
        _body.Content = sp;
    }

    FrameworkElement Tiles(List<GameItem> games)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var g in games)
        {
            bool on = _picked.Contains(g.Game.Id);
            var pic = new Border { Height = 74, CornerRadius = new CornerRadius(8, 8, 0, 0), ClipToBounds = true, Background = Res("SurfaceBrush") };
            if (g.CoverImage != null) pic.Background = new ImageBrush(g.CoverImage) { Stretch = Stretch.UniformToFill };
            else if (g.Icon != null) pic.Child = new Image { Source = g.Icon, Width = 36, Height = 36 };
            else pic.Child = new TextBlock { Text = g.Game.Short, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = g.Game.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 7, 26, 8) };
            var check = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 8, 8),
                Background = on ? Res("AccentBrush") : Res("SurfaceBrush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(on ? 0 : 1),
                Child = new TextBlock { Text = on ? "✓" : "", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Res("OnAccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var inner = new Grid();
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inner.Children.Add(pic);
            Grid.SetRow(name, 1); inner.Children.Add(name);
            Grid.SetRow(check, 1); inner.Children.Add(check);
            var tile = new Border
            {
                Width = 152, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(9), Background = Res("SurfaceBrush"), Cursor = Cursors.Hand, Child = inner,
                BorderBrush = on ? Res("AccentBrush") : Res("BorderBrush"), BorderThickness = new Thickness(on ? 2 : 1), Opacity = on ? 1 : 0.75,
            };
            var id = g.Game.Id;
            tile.MouseLeftButtonUp += (_, _) => { if (!_picked.Remove(id)) _picked.Add(id); Render(); };
            w.Children.Add(tile);
        }
        return w;
    }

    // ───────── check ─────────

    void Check()
    {
        Head("Check your PC", "A one-minute scan for the settings and hardware problems that cost the most frames. It only reads; nothing changes.", _findings == null ? "Skip this step" : "Next");
        var sp = new StackPanel();
        if (_findings == null)
        {
            var run = new Button { Style = (Style)Application.Current.FindResource("PrimaryButton"), Content = Loc.T(_checking ? "Checking…" : "Run the check"), IsEnabled = !_checking, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(20, 9, 20, 10) };
            var progress = new TextBlock { FontSize = 12.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 12, 0, 0), Text = _checking ? Loc.T("Starting…") : "" };
            run.Click += async (_, _) =>
            {
                _checking = true;
                Render();
                var prog = new Progress<string>(s => { if (_body.Content is StackPanel p && p.Children.Count > 1 && p.Children[1] is TextBlock t) t.Text = Loc.T(s) + "…"; });
                try { _findings = await Task.Run(() => PcDiagnostics.RunAll(prog)); }
                catch { _findings = new List<Finding>(); }
                _checking = false;
                Render();
            };
            sp.Children.Add(run);
            sp.Children.Add(progress);
            _body.Content = sp;
            return;
        }
        int problems = _findings.Count(f => f.Severity == Severity.Problem), checks = _findings.Count(f => f.Severity == Severity.Check), ok = _findings.Count(f => f.Severity == Severity.Ok);
        sp.Children.Add(new TextBlock
        {
            Text = problems + checks == 0 ? Loc.T("Nothing to fix: this PC is set up well.") : Loc.F("Problems: {0} · Worth checking: {1} · Fine: {2}", problems, checks, ok),
            FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res(problems > 0 ? "WarnBrush" : "GoodBrush"), TextWrapping = TextWrapping.Wrap,
        });
        foreach (var f in _findings.Where(f => f.Severity is Severity.Problem or Severity.Check).Take(5))
        {
            var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            bool bad = f.Severity == Severity.Problem;
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2, 9, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Background = Res(bad ? "BadSoftBrush" : "WarnSoftBrush"),
                Child = new TextBlock { Text = Loc.T(bad ? "Problem found" : "Worth checking"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res(bad ? "BadBrush" : "WarnBrush") },
            });
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = Loc.T(f.Title), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(f.Detail)) t.Children.Add(new TextBlock { Text = Loc.T(f.Detail), FontSize = 12, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(t, 1);
            row.Children.Add(t);
            sp.Children.Add(row);
        }
        if (problems + checks > 0)
            sp.Children.Add(Dim(Loc.T("Step-by-step fixes for each one are on the Diagnose page. Pulse will open it when setup is done.")).Also(d => d.Margin = new Thickness(0, 16, 0, 0)));
        _body.Content = sp;
    }

    // ───────── tweaks ─────────

    void Tweaks()
    {
        _tweaks ??= TweakCatalog.All.Where(t => t.Recommended && t.GateStatus.Met && !t.IsOn).ToList();
        if (_tweakPicked.Count == 0 && _applied < 0) foreach (var t in _tweaks) _tweakPicked.Add(t.Id);
        if (_applied >= 0)
        {
            Head("Tweaks applied", null, "Next");
            var done = new StackPanel();
            done.Children.Add(new TextBlock { Text = Loc.F("{0} tweaks are on. Each one is backed up: Tweaks or Undo & restore turns any of them off again.", _applied), FontSize = 15, Foreground = Res("GoodBrush"), TextWrapping = TextWrapping.Wrap });
            done.Children.Add(Dim(Loc.T("Some take full effect after you sign out or restart.")).Also(d => d.Margin = new Thickness(0, 8, 0, 0)));
            _body.Content = done;
            return;
        }
        if (_tweaks.Count == 0)
        {
            Head("Recommended tweaks", null, "Next");
            _body.Content = new TextBlock { Text = Loc.T("All the recommended tweaks for this PC are already on."), FontSize = 15, Foreground = Res("GoodBrush"), TextWrapping = TextWrapping.Wrap };
            return;
        }
        Head("Apply the recommended tweaks", "The safe, documented settings that fit this PC. Pulse makes a restore point first and backs up each change. Untick any you'd rather leave.", "Skip this step");
        var sp = new StackPanel();
        var apply = new Button { Style = (Style)Application.Current.FindResource("PrimaryButton"), Content = _applying ? Loc.T("Applying…") : Loc.F("Apply the {0} ticked tweaks", _tweakPicked.Count), IsEnabled = !_applying, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(20, 9, 20, 10) };
        var status = new TextBlock { FontSize = 12.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 8, 0, 14) };
        sp.Children.Add(apply);
        sp.Children.Add(status);
        foreach (var t in _tweaks)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var cb = new CheckBox { IsChecked = _tweakPicked.Contains(t.Id), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0), IsEnabled = !_applying };
            var id = t.Id;
            cb.Click += (_, _) => { if (cb.IsChecked == true) _tweakPicked.Add(id); else _tweakPicked.Remove(id); apply.Content = Loc.F("Apply the {0} ticked tweaks", _tweakPicked.Count); };
            row.Children.Add(cb);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = Loc.T(t.Title), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = Loc.T(t.Summary), FontSize = 12, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            sp.Children.Add(row);
        }
        apply.Click += async (_, _) =>
        {
            var todo = _tweaks.Where(t => _tweakPicked.Contains(t.Id)).ToList();
            if (todo.Count == 0) { Go(_step + 1); return; }
            _applying = true;
            Render();
            await TweakRunner.EnsureRestorePoint();
            int ok = 0;
            foreach (var t in todo)
            {
                status.Text = Loc.T(t.Title) + "…";
                try { await Task.Run(() => TweakEngine.Apply(t)); t.IsOn = true; ok++; } catch { }
            }
            _applying = false;
            _applied = ok;
            Render();
        };
        _body.Content = sp;
    }

    // ───────── you ─────────

    void You()
    {
        Head("Tell Pulse who you are", "So your stats, matches and after-match summaries are yours from the start.", "Next");
        var sp = new StackPanel();
        if (_picked.Contains("valorant"))
        {
            sp.Children.Add(Caption("VALORANT"));
            sp.Children.Add(Dim(Loc.T("Nothing to do: Pulse finds your account through the Riot Client while it's open, so your tracker and match summaries are yours automatically.")));
        }
        if (_picked.Contains("brawlhalla"))
        {
            sp.Children.Add(Caption("Brawlhalla").Also(c => c.Margin = new Thickness(0, _picked.Contains("valorant") ? 22 : 0, 0, 0)));
            var s = TweakEngine.State.Brawlhalla ??= new BrawlhallaSettings();
            if (s.MyId > 0)
            {
                sp.Children.Add(new TextBlock { Text = Loc.F("You're {0}. Pulse logs your matches while Brawlhalla is open.", s.MyName is { Length: > 0 } n ? n : s.MyId.ToString(CultureInfo.InvariantCulture)), FontSize = 13.5, Foreground = Res("GoodBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
                var change = new Button { Style = (Style)Application.Current.FindResource("LinkButton"), Content = Loc.T("That's not me"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
                change.Click += (_, _) => { s.MyId = 0; s.MyName = null; TweakEngine.Save(); Render(); };
                sp.Children.Add(change);
            }
            else
            {
                sp.Children.Add(Dim(Loc.T("Your Brawlhalla name (if you play ranked) or your Brawlhalla ID (in game: Inventory, Profile). Pulse then logs your matches and shows a summary after each one.")));
                var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 380 });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var box = new TextBox { Style = (Style)Application.Current.FindResource("SearchBox"), Tag = Loc.T("Name or Brawlhalla ID"), FontSize = 14 };
                row.Children.Add(box);
                var find = new Button { Style = (Style)Application.Current.FindResource("GhostButton"), Content = Loc.T(_bhBusy ? "Searching…" : "Find me"), IsEnabled = !_bhBusy, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(16, 6, 16, 7) };
                Grid.SetColumn(find, 1);
                row.Children.Add(find);
                async Task Search()
                {
                    var text = box.Text.Trim();
                    if (text.Length < 2) return;
                    _bhBusy = true; _bhStatus = null; Render();
                    try
                    {
                        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                        {
                            var p = await Task.Run(() => Brawlhalla.Profile(id, lookUpTier: false));
                            if (p == null) _bhStatus = Loc.T("Brawlhalla's servers sent no stats for that player. If the ID is right, try again in a moment.");
                            else { s.MyId = p.Id; s.MyName = p.Name; TweakEngine.Save(); }
                        }
                        else
                        {
                            _bhResults = await Task.Run(() => Brawlhalla.Find(text));
                            if (_bhResults.Count == 0) _bhStatus = Loc.T("No ranked player with that name. Try their Brawlhalla ID instead.");
                        }
                    }
                    catch { _bhStatus = Loc.T("Couldn't reach Brawlhalla's stats. Try again in a moment."); }
                    _bhBusy = false;
                    Render();
                }
                find.Click += (_, _) => _ = Search();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = Search(); } };
                sp.Children.Add(row);
                if (_bhStatus != null) sp.Children.Add(new TextBlock { Text = _bhStatus, FontSize = 12, Foreground = Res("BadBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
                if (_bhResults is { Count: > 0 })
                    foreach (var r in _bhResults.Take(6))
                    {
                        var pick = new Button { Style = (Style)Application.Current.FindResource("GhostButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(14, 6, 14, 7),
                            Content = $"{r.Name}  ·  {r.Tier} {r.Rating:N0}  ·  {r.Region}" };
                        pick.Click += (_, _) => { s.MyId = r.Id; s.MyName = r.Name; _bhResults = null; TweakEngine.Save(); Render(); };
                        sp.Children.Add(pick);
                    }
            }
        }
        _body.Content = sp;
    }

    // ───────── done ─────────

    void Done()
    {
        Head("You're set", "Here's where to go next for what you picked.", "Finish");
        var w = new WrapPanel();
        var cards = new List<(string Page, object Param, string Title, string Text)>();
        if (_findings?.Any(f => f.Severity is Severity.Problem or Severity.Check) == true)
            cards.Add(("diagnose", _findings, "Fix what the check found", "Step-by-step fixes for each problem, most impactful first."));
        if (_picked.Contains("valorant")) cards.Add(("valorant", null, "VALORANT tracker", "Your matches, ranks, agents and a summary after every game."));
        if (_picked.Contains("brawlhalla")) cards.Add(("brawlhalla", null, "Brawlhalla tracker", "Your legends, ranked and matches. Ctrl+Shift+B in game checks your opponent."));
        if (_picked.Contains("roblox")) cards.Add(("roblox", null, "Roblox", "FPS unlock and settings that make Roblox smoother."));
        var other = _picked.Where(id => id is not ("valorant" or "brawlhalla" or "roblox")).ToList();
        if (other.Count > 0)
        {
            var name = GameCatalog.All.FirstOrDefault(g => g.Id == other[0])?.Name ?? "";
            cards.Add(("games", other[0], other.Count == 1 ? Loc.F("{0} settings", name) : "Your game settings", "The settings that raise FPS and lower input lag, game by game."));
        }
        cards.Add(("clips", null, "Clips", "Save your best plays with a hotkey, like ShadowPlay or Medal."));
        cards.Add(("crosshair", null, "Crosshair", "A custom crosshair over any game, or copy a pro's."));
        cards.Add(("guide", null, "PC guide", "What your PC can run, upgrade ideas, prices and step-by-step guides."));
        foreach (var (page, param, title, text) in cards.Take(6))
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            sp.Children.Add(new TextBlock { Text = Loc.T(text), FontSize = 12.5, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            var card = new Border { Width = 260, MinHeight = 96, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(10), Background = Res("SurfaceBrush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = sp };
            card.MouseEnter += (_, _) => card.BorderBrush = Res("AccentBrush");
            card.MouseLeave += (_, _) => card.BorderBrush = Res("BorderBrush");
            card.MouseLeftButtonUp += (_, _) => Finish(page, param);
            w.Children.Add(card);
        }
        _body.Content = w;
    }

    void Finish(string page = null, object param = null)
    {
        S.Done = true;
        if (_steps.Contains("games")) S.Games = _picked.ToList();
        TweakEngine.Save();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            Closed?.Invoke();
            if (page != null) MainWindow.Instance?.Navigate(page, param);
            else if (_findings?.Any(f => f.Severity is Severity.Problem or Severity.Check) == true) MainWindow.Instance?.Navigate("diagnose", _findings);
        };
        BeginAnimation(OpacityProperty, fade);
    }

    static TextBlock Caption(string text) => new() { Text = text.ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 0, 0, 4) };
    static TextBlock Dim(string text) => new() { Text = text, FontSize = 13, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
}
