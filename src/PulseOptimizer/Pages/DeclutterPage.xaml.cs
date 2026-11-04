using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Pulse.Core;
using Pulse.Core.Declutter;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>One row in Quick clean: a cleanup category, Windows' component cleanup, or the hibernation file.</summary>
public class QuickItem : Observable
{
    public string Name { get; init; }
    public string Glyph { get; init; }
    public string Description { get; init; }
    public string Warning { get; init; } = "";
    public string ActionText { get; init; } = "";
    public string ActionKey { get; init; } = "";
    public CleanCategory Category { get; init; }
    public string Kind { get; init; } = "category"; // category | dism | hibernate | info

    bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool CanPick => Kind != "info";

    long _size = -1;
    /// <summary>-1 while scanning; for DISM, an estimate isn't possible so it stays 0 and SizeText explains.</summary>
    public long Size { get => _size; set { if (Set(ref _size, value)) Raise(nameof(SizeText)); } }
    string _sizeOverride;
    public string SizeOverride { get => _sizeOverride; set { if (Set(ref _sizeOverride, value)) Raise(nameof(SizeText)); } }
    public string SizeText => _sizeOverride ?? (Size < 0 ? "Scanning…" : Size == 0 ? "Clean" : Format.Bytes(Size));
}

public partial class DeclutterPage : UserControl, IPage
{
    readonly List<QuickItem> _quick = new();
    List<ReviewFile> _installers = new(), _big = new();
    List<AppEntry> _apps = new();
    bool _scanned, _busy;
    CancellationTokenSource _cancel;

    public DeclutterPage()
    {
        InitializeComponent();
        foreach (var c in Cleaner.Categories)
            _quick.Add(new QuickItem { Name = c.Name, Glyph = c.Glyph, Description = c.Description, Warning = c.Warning, Category = c, Selected = c.Selected });
        QuickList.ItemsSource = _quick;
        DriveLabel.Text = $"YOUR {SystemInfo.SystemDrive.Name.TrimEnd('\\')} DRIVE";
        BuildGoalChips();
    }

    public async void OnShown(object parameter)
    {
        if (!_scanned) await Scan();
    }

    async void Rescan_Click(object sender, RoutedEventArgs e) => await Scan();

    async Task Scan()
    {
        if (_busy) return;
        _busy = true;
        _cancel?.Cancel();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        FreeBtn.IsEnabled = RescanBtn.IsEnabled = false;
        FreeableBig.Text = "—";
        FreeableSmall.Text = "  scanning…";
        MapLoading.Visibility = Visibility.Visible;
        MapStatus.Text = "Finding clutter…";
        UpdateDriveText();

        // Extra rows that depend on this PC.
        _quick.RemoveAll(q => q.Kind != "category");
        foreach (var q in _quick) q.Size = -1;
        var hib = TweakCatalog.All.FirstOrDefault(t => t.Id == "hibernate");
        long hibSize = await Task.Run(DeclutterEngine.HibernationFileSize);
        if (hibSize > 0 && hib != null && !hib.IsOn)
            _quick.Add(new QuickItem
            {
                Kind = "hibernate", Name = "Hibernation file", Glyph = "", Size = hibSize,
                Description = "Hidden file Windows keeps for hibernate and Fast Startup. If you never hibernate, the space is wasted. Turning it off can be undone from Tweaks.",
                Warning = SystemInfo.HasBattery ? "On a laptop, hibernate saves battery when the lid stays closed. Leave this off unless you never use it." : "Cold boots may take a few seconds longer without Fast Startup.",
                Selected = false,
            });
        _quick.Add(new QuickItem
        {
            Kind = "dism", Name = "Old Windows update files", Glyph = "", SizeOverride = "Checking…",
            Description = "Older copies of Windows components left behind by updates. Pulse uses Microsoft's own cleanup command (DISM). Updates you have now can still be uninstalled afterwards. Takes 5 to 20 minutes.",
            Selected = false,
        });
        long oldWin = await Task.Run(() => DeclutterEngine.WindowsOldSize(token));
        if (oldWin > 0)
            _quick.Add(new QuickItem
            {
                Kind = "info", Name = "Previous Windows installation", Glyph = "", Size = oldWin,
                Description = "Windows.old is kept for 10 days after a big Windows upgrade so you can go back. Windows removes it itself; you can remove it sooner in Windows Settings.",
                ActionText = "Open Windows cleanup", ActionKey = "settings:ms-settings:storagesense",
            });
        QuickList.ItemsSource = null;
        QuickList.ItemsSource = _quick;

        // Scan everything in parallel; cards update as their own scan finishes.
        var catTask = Task.WhenAll(_quick.Where(q => q.Kind == "category").Select(q => Task.Run(() =>
        {
            Cleaner.Scan(q.Category);
            q.Size = q.Category.Size;
        })));
        var dismTask = Task.Run(() => DeclutterEngine.AnalyzeComponentStore());
        var filesTask = Task.Run(() => (DeclutterEngine.OldInstallers(), DeclutterEngine.LargeOldFiles(token)));
        var appsTask = Task.Run(DeclutterEngine.Apps);

        AppsLoading.Visibility = Visibility.Visible;
        _ = appsTask.ContinueWith(t => Dispatcher.Invoke(() =>
        {
            _apps = t.IsCompletedSuccessfully ? t.Result : new();
            AppsLoading.Visibility = Visibility.Collapsed;
            AppsSummary.Text = $"{_apps.Count} apps · {Format.Bytes(_apps.Sum(a => a.Size))}";
            ShowApps();
        }));
        _ = dismTask.ContinueWith(t => Dispatcher.Invoke(() =>
        {
            var d = _quick.FirstOrDefault(q => q.Kind == "dism");
            if (d == null) return;
            var (recommended, reclaimable) = t.IsCompletedSuccessfully ? t.Result : (false, 0L);
            d.Size = reclaimable;
            d.SizeOverride = recommended ? (reclaimable > 0 ? "About " + Format.Bytes(reclaimable) : "Recommended") : "Already tidy";
            d.Selected = recommended;
            UpdateTotal();
        }));

        await catTask;
        var (inst, big) = await filesTask;
        _installers = inst; _big = big;
        InstallerList.ItemsSource = _installers;
        BigList.ItemsSource = _big;
        InstallersCard.Visibility = _installers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BigCard.Visibility = _big.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _scanned = true;
        _busy = false;
        RescanBtn.IsEnabled = true;
        UpdateTotal();

        // Storage map last: it walks big folders and can take a while.
        MapStatus.Text = "Measuring your drive…";
        long clutter = _quick.Where(q => q.Kind == "category" && q.Size > 0 && !q.Category.IsRecycleBin).Sum(q => q.Size) + _installers.Sum(f => f.Size);
        var progress = new Progress<string>(s => MapStatus.Text = s);
        var map = await Task.Run(() => DeclutterEngine.Map(clutter, progress, token));
        if (!token.IsCancellationRequested) DrawMap(map);
    }

    // ───────── Totals ─────────

    long SelectedBytes =>
        _quick.Where(q => q.Selected && q.CanPick && q.Size > 0).Sum(q => q.Size)
        + _installers.Where(f => f.Selected).Sum(f => f.Size)
        + _big.Where(f => f.Selected).Sum(f => f.Size);

    void UpdateTotal()
    {
        long total = SelectedBytes;
        bool any = total > 0 || _quick.Any(q => q.Selected && q.Kind == "dism");
        FreeableBig.Text = total > 0 ? Format.Bytes(total) : "0 GB";
        long inUse = _quick.Where(q => q.Kind == "category").Sum(q => q.Category.InUseSize);
        FreeableSmall.Text = "  " + Loc.T("ready to free") + (inUse > 1 << 20 ? "  ·  " + Loc.F("{0} in use by open apps (frees after a restart)", Format.Bytes(inUse)) : "");
        FreeBtnText.Text = total > 0 ? $"Free {Format.Bytes(total)}" : "Free space";
        FreeBtn.IsEnabled = any && !_busy;
    }

    void UpdateDriveText()
    {
        var d = SystemInfo.SystemDrive;
        double pctFree = d.TotalSize > 0 ? (double)d.AvailableFreeSpace / d.TotalSize : 1;
        DriveText.Text = $"{Format.Bytes(d.AvailableFreeSpace)} free of {Format.Bytes(d.TotalSize, 0)}" +
            (pctFree < 0.1 ? ". Your drive is nearly full, which slows updates and can cause stutter while games load." : ".");
    }

    void Pick_Click(object sender, RoutedEventArgs e) => UpdateTotal();

    /// <summary>Clicking anywhere on a row ticks it (except its own buttons).</summary>
    void Row_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case QuickItem q when q.CanPick: q.Selected = !q.Selected; UpdateTotal(); break;
            case ReviewFile f: f.Selected = !f.Selected; UpdateTotal(); break;
            case AppEntry a when a.CanRemove && !a.Busy: a.Selected = !a.Selected; UpdateBulk(); break;
        }
    }

    void Action_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.RunAction((sender as FrameworkElement)?.Tag as string);

    void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    // ───────── Storage map ─────────

    void DrawMap(StorageMap map)
    {
        MapLoading.Visibility = Visibility.Collapsed;
        MapBar.Children.Clear();
        MapBar.ColumnDefinitions.Clear();
        MapLegend.Children.Clear();
        var parts = map.Slices.Where(s => s.Bytes > 0).Append(new StorageSlice("Free", map.Free, "#2A2F3D")).ToList();
        double all = Math.Max(parts.Sum(p => (double)p.Bytes), 1);
        int i = 0;
        foreach (var s in parts)
        {
            // Widths as tenths of a percent: raw byte counts (hundreds of billions) are too large for the layout,
            // which drew only the first part. Every part keeps at least a sliver so its colour shows.
            MapBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(s.Bytes / all * 1000, 4), GridUnitType.Star) });
            var brush = (Brush)new BrushConverter().ConvertFromString(s.Color);
            var seg = new Border { Background = brush, Margin = new Thickness(0, 0, i < parts.Count - 1 ? 2 : 0, 0), RenderTransformOrigin = new Point(0, 0.5) };
            var scale = new ScaleTransform(0, 1);
            seg.RenderTransform = scale;
            Grid.SetColumn(seg, i);
            MapBar.Children.Add(seg);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(550))
            { BeginTime = TimeSpan.FromMilliseconds(70 * i), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(3), Background = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextDimBrush") };
            label.Inlines.Add(new System.Windows.Documents.Run(s.Name + "  "));
            label.Inlines.Add(new System.Windows.Documents.Run(Format.Bytes(s.Bytes)) { Foreground = (Brush)FindResource("TextBrush"), FontWeight = FontWeights.SemiBold });
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 6) };
            item.Children.Add(dot); item.Children.Add(label);
            MapLegend.Children.Add(item);
            i++;
        }
    }

    // ───────── Free space ─────────

    async void Free_Click(object sender, RoutedEventArgs e)
    {
        var cats = _quick.Where(q => q.Kind == "category" && q.Selected && q.Size > 0).ToList();
        var files = _installers.Concat(_big).Where(f => f.Selected).ToList();
        var hib = _quick.FirstOrDefault(q => q.Kind == "hibernate" && q.Selected);
        var dism = _quick.FirstOrDefault(q => q.Kind == "dism" && q.Selected);
        if (cats.Count == 0 && files.Count == 0 && hib == null && dism == null)
        {
            MainWindow.Instance.Toast("Nothing is ticked yet.", ToastKind.Info);
            return;
        }

        var lines = new List<string>();
        if (cats.Count > 0) lines.Add($"•  Delete {Format.Bytes(cats.Sum(c => c.Size))} of temporary and cache files");
        if (files.Count > 0) lines.Add($"•  Move {files.Count} file{(files.Count == 1 ? "" : "s")} ({Format.Bytes(files.Sum(f => f.Size))}) to the Recycle Bin");
        if (hib != null) lines.Add($"•  Turn off hibernation ({Format.Bytes(hib.Size)})");
        if (dism != null) lines.Add("•  Clean old Windows update files (5 to 20 minutes)");
        if (!await MainWindow.Instance.Confirm("Free up space?", string.Join("\n", lines) + "\n\nFiles that are in use are skipped.", "Free space"))
            return;

        _busy = true;
        FreeBtn.IsEnabled = RescanBtn.IsEnabled = false;

        // One line per job, so people can watch it work instead of wondering if the button did anything.
        var steps = new List<(ProgressStep Step, Func<ProgressStep, Task> Run)>();
        long freed = 0, inUse = 0, recycled = 0;
        string dismNote = "";
        foreach (var c in cats)
            steps.Add((new ProgressStep(c.Name) { Weight = 0.5 + c.Category.Count / 3000.0 + c.Size / 300e6 }, async st =>
            {
                long size = c.Size;
                long got = await Task.Run(() => Cleaner.Clean(c.Category, f => Dispatcher.InvokeAsync(() =>
                {
                    st.Fraction = size > 0 ? (double)f / size : 0;
                    st.Detail = Loc.F("{0} of {1}", Format.Bytes(f), Format.Bytes(size));
                })));
                freed += got;
                long left = Math.Max(size - got, 0);
                inUse += left;
                if (got == 0 && left > 0) st.Skip(Loc.T("In use by open apps"));
                else if (left > 0) st.Finish(Loc.F("{0} freed · {1} in use", Format.Bytes(got), Format.Bytes(left)));
                else st.Finish(Loc.F("{0} freed", Format.Bytes(got)));
            }));
        if (files.Count > 0)
            steps.Add((new ProgressStep("Move files to the Recycle Bin") { Weight = 1 + files.Count * 0.15 }, async st =>
            {
                recycled = await DeclutterEngine.RecycleAsync(files);
                st.Finish(Loc.F("{0} moved", Format.Bytes(recycled)));
            }));
        if (hib != null)
            steps.Add((new ProgressStep("Turn off hibernation") { Weight = 3 }, async st =>
            {
                // The tweak has its own confirmation; step aside for it, then come back.
                MainWindow.Instance.HideOverlay();
                var t = TweakCatalog.All.First(x => x.Id == "hibernate");
                bool done = await TweakRunner.Set(t, true);
                MainWindow.Instance.ShowSteps("Cleaning your PC", "You can keep using your PC while this runs.", steps.Select(x => x.Step).ToList());
                if (done) { freed += hib.Size; st.Finish(Loc.F("{0} freed", Format.Bytes(hib.Size))); }
                else st.Skip(Loc.T("Not changed"));
            }));
        if (dism != null)
            steps.Add((new ProgressStep("Clean old Windows update files") { Weight = 600 }, async st =>
            {
                st.Detail = Loc.T("5 to 20 minutes…");
                long before = SystemInfo.SystemDrive.AvailableFreeSpace;
                // Windows' tool doesn't say how far it is: move the bar with the time it usually takes (about 10 minutes).
                var clock = Stopwatch.StartNew();
                var work = Task.Run(DeclutterEngine.CleanWindowsUpdateFiles);
                while (!work.IsCompleted)
                {
                    await Task.WhenAny(work, Task.Delay(1000));
                    st.Fraction = Math.Min(clock.Elapsed.TotalMinutes / 10, 0.95);
                    st.Detail = Loc.F("{0} so far · usually 5 to 20 minutes", clock.Elapsed.ToString(@"m\:ss"));
                }
                var (ok, _, msg) = await work;
                long got = Math.Max(SystemInfo.SystemDrive.AvailableFreeSpace - before, 0);
                freed += got;
                if (ok) st.Finish(got > 0 ? Loc.F("{0} freed", Format.Bytes(got)) : Loc.T("Done"));
                else { st.Skip(Loc.T("Didn't finish")); dismNote = Loc.T("Windows' update cleanup didn't finish:") + " " + msg; }
            }));

        MainWindow.Instance.ShowSteps("Cleaning your PC", "You can keep using your PC while this runs.", steps.Select(x => x.Step).ToList());
        try
        {
            foreach (var (st, run) in steps)
            {
                st.Start();
                // Keep each line on screen long enough to read, even when deleting takes a split second.
                await Task.WhenAll(run(st), Task.Delay(450));
            }
            TweakEngine.AddLog("clean", $"Declutter freed {Format.Bytes(freed)}");

            var notes = new List<string>();
            if (inUse > 0) notes.Add(Loc.F("{0} was in use by open apps, so Pulse left it alone. It usually clears after you restart your PC.", Format.Bytes(inUse)));
            if (recycled > 0) notes.Add(Loc.F("{0} is in the Recycle Bin. Empty it when you're sure, to get that space back too.", Format.Bytes(recycled)));
            if (dismNote.Length > 0) notes.Add(dismNote);
            string title = freed + recycled > 0 ? Loc.F("Freed {0}", Format.Bytes(freed + recycled)) : Loc.T("Nothing could be removed right now");
            await MainWindow.Instance.FinishSteps(title, notes.Count > 0 ? string.Join("\n\n", notes) : Loc.T("All done. Your PC is clean."), ok: freed + recycled > 0 || inUse == 0);
        }
        catch (Exception ex)
        {
            await MainWindow.Instance.FinishSteps("Cleanup stopped", ex.Message, ok: false);
        }
        finally
        {
            _busy = false;
            _scanned = false;
        }
        await Scan();
    }

    // ───────── Apps ─────────

    string _tab = "Suggested";
    int _shown = 40;

    IEnumerable<AppEntry> FilteredApps()
    {
        var q = AppSearch?.Text?.Trim() ?? "";
        IEnumerable<AppEntry> list = _tab switch
        {
            "Suggested" => _apps.Where(a => a.Suggested),
            "Game" => _apps.Where(a => a.Kind == "Game"),
            "Leftover" => _apps.Where(a => a.Leftover && !a.Removed),
            _ => _apps.Where(a => !a.Leftover).OrderByDescending(a => a.Size),
        };
        if (q.Length > 0) list = _apps.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)).OrderByDescending(a => a.Size);
        return list;
    }

    void ShowApps()
    {
        if (AppList == null) return;
        var all = FilteredApps().ToList();
        if (LeftoverTab != null) LeftoverTab.Visibility = _apps.Any(a => a.Leftover && !a.Removed) ? Visibility.Visible : Visibility.Collapsed;
        AppList.ItemsSource = all.Take(_shown).ToList();
        AppsEmpty.Text = _tab == "Suggested" && AppSearch.Text.Length == 0 ? "No preinstalled extras found. Check the Games and All programs tabs." : "Nothing matches.";
        AppsEmpty.Visibility = all.Count == 0 && AppsLoading.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        ShowMoreBtn.Visibility = all.Count > _shown ? Visibility.Visible : Visibility.Collapsed;
        ShowMoreBtn.Content = $"Show {Math.Min(40, all.Count - _shown)} more";
        UpdateBulk();
    }

    void AppTab_Checked(object sender, RoutedEventArgs e)
    {
        _tab = (sender as FrameworkElement)?.Tag as string ?? "Suggested";
        _shown = 40;
        ShowApps();
    }

    void AppSearch_Changed(object sender, TextChangedEventArgs e) { _shown = 40; ShowApps(); }
    void ShowMore_Click(object sender, RoutedEventArgs e) { _shown += 40; ShowApps(); }
    void AppPick_Click(object sender, RoutedEventArgs e) => UpdateBulk();

    void BulkClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _apps) a.Selected = false;
        UpdateBulk();
    }

    void UpdateBulk()
    {
        var picked = _apps.Where(a => a.Selected).ToList();
        BulkBar.Visibility = picked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BulkText.Text = $"{picked.Count} selected · {Format.Bytes(picked.Sum(a => a.Size))}";
        BulkBtnText.Text = $"Remove {picked.Count}";
    }

    async void BulkRemove_Click(object sender, RoutedEventArgs e)
    {
        var picked = _apps.Where(a => a.Selected && a.CanRemove).ToList();
        if (picked.Count == 0) return;
        int store = picked.Count(a => a.IsStoreApp), other = picked.Count - store;
        var lines = string.Join("\n", picked.Take(12).Select(a => $"•  {a.Name}{(a.Size > 0 ? $" ({Format.Bytes(a.Size)})" : "")}"))
                    + (picked.Count > 12 ? $"\n…and {picked.Count - 12} more" : "");
        var note = other > 0
            ? $"\n\nPulse runs each program's own uninstaller one at a time. Some show a window you need to click through.{(picked.Any(a => !a.Wait) ? " Steam games are removed through Steam, which asks you to confirm." : "")}"
            : "\n\nStore apps can be reinstalled from the Microsoft Store any time.";
        if (!await MainWindow.Instance.Confirm($"Remove {picked.Count} app{(picked.Count == 1 ? "" : "s")}?", lines + note, "Remove")) return;

        BulkBtn.IsEnabled = false;
        long before = SystemInfo.SystemDrive.AvailableFreeSpace;
        int done = 0, failed = 0;
        MainWindow.Instance.ShowBusy("Removing apps", "Keep Pulse open until this finishes.");
        foreach (var app in picked)
        {
            MainWindow.Instance.SetBusyStatus($"{done + failed + 1} of {picked.Count}: {app.Name}…");
            app.Busy = true;
            var (ok, _) = await Task.Run(() => app.IsStoreApp ? DeclutterEngine.RemoveStoreApp(app) : DeclutterEngine.StartUninstaller(app));
            bool gone = ok && (app.IsStoreApp || !app.Wait || !await Task.Run(() => DeclutterEngine.StillInstalled(app)));
            app.Busy = false;
            if (gone) { app.Removed = true; done++; } else failed++;
        }
        MainWindow.Instance.HideOverlay();
        BulkBtn.IsEnabled = true;
        long freed = Math.Max(SystemInfo.SystemDrive.AvailableFreeSpace - before, 0);
        TweakEngine.AddLog("apps", $"Removed {done} apps, freed {Format.Bytes(freed)}");
        UpdateBulk();
        UpdateDriveText();
        await MainWindow.Instance.Alert($"Removed {done} of {picked.Count}",
            $"Freed {Format.Bytes(freed)}." + (failed > 0 ? $" {failed} {(failed == 1 ? "is" : "are")} still installed: the uninstaller was cancelled or needs a restart to finish." : ""));
    }

    async void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppEntry app || app.Busy) return;
        var text = app.Leftover
            ? Loc.T("This program is already deleted. Only its entry in Windows' list of installed programs is left, so Pulse removes just that entry. Pulse saves a backup of it first.")
            : app.IsStoreApp
            ? $"{(app.Why.Length > 0 ? app.Why + ". " : "")}It's removed for your account and can be reinstalled from the Microsoft Store any time."
            : $"Pulse runs {app.Name}'s own uninstaller{(app.Wait ? "; follow its steps if it shows a window" : " through Steam")}.";
        if (!await MainWindow.Instance.Confirm(app.Leftover ? Loc.F("Remove {0} from the list?", app.Name) : $"Remove {app.Name}?", text, "Remove")) return;

        app.Busy = true;
        try
        {
            var (ok, msg) = await Task.Run(() => app.IsStoreApp ? DeclutterEngine.RemoveStoreApp(app) : DeclutterEngine.StartUninstaller(app));
            if (!ok) { await MainWindow.Instance.Alert($"Couldn't remove {app.Name}", msg); return; }
            if (app.Leftover)
            {
                app.Removed = true;
                MainWindow.Instance.Toast(Loc.F("Removed {0} from the list.", app.Name));
                ShowApps();
                return;
            }
            if (app.IsStoreApp || (app.Wait && !await Task.Run(() => DeclutterEngine.StillInstalled(app))))
            {
                app.Removed = true;
                MainWindow.Instance.Toast($"Removed {app.Name}.");
                UpdateDriveText();
            }
            else MainWindow.Instance.Toast(app.Wait ? $"{app.Name} is still installed. The uninstaller may have been cancelled." : $"Confirm the uninstall in Steam.", ToastKind.Info);
        }
        finally { app.Busy = false; UpdateBulk(); }
    }

    // ───────── Free up to a goal ─────────

    List<GoalItem> _goalItems = new();
    CancellationTokenSource _goalCancel;
    int _goalShown = 25;
    bool _goalBusy;

    long GoalBytes => double.TryParse(GoalBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var gb) && gb > 0
        ? (long)(gb * 1073741824.0) : 0;

    static long FreeNow => SystemInfo.SystemDrive.AvailableFreeSpace;

    void BuildGoalChips()
    {
        long freeGb = FreeNow >> 30;
        // Suggestions above what's free now, rounded to familiar sizes.
        var picks = new[] { 25, 50, 100, 200, 500 }.Where(g => g > freeGb).Take(3).ToList();
        if (picks.Count == 0) picks.Add((int)freeGb + 50);
        GoalChips.Children.Clear();
        foreach (var g in picks)
        {
            var b = new Button { Style = (Style)FindResource("GhostButton"), Content = $"{g} GB", Margin = new Thickness(0, 0, 6, 0), Tag = g };
            b.Click += (s, _) => GoalBox.Text = ((int)((FrameworkElement)s).Tag).ToString(System.Globalization.CultureInfo.CurrentCulture);
            GoalChips.Children.Add(b);
        }
        GoalBox.Text = picks[0].ToString(System.Globalization.CultureInfo.CurrentCulture);
    }

    void GoalBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded && GoalStatus == null) return;
        UpdateGoal();
    }

    async void GoalPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_goalBusy) return;
        if (GoalBytes <= 0) { GoalStatus.Text = Loc.T("Type how many GB you want free."); return; }
        if (SpaceGoal.Needed(GoalBytes, FreeNow) == 0) { GoalStatus.Text = Loc.F("You already have {0} free. Nothing to do.", Format.Bytes(FreeNow)); return; }
        _goalBusy = true;
        _goalCancel = new CancellationTokenSource();
        GoalPlanBtn.IsEnabled = false;
        GoalCancelBtn.Visibility = Visibility.Visible;
        GoalPanel.Visibility = Visibility.Collapsed;
        var progress = new Progress<string>(t => GoalStatus.Text = t);
        try
        {
            _goalItems = await Task.Run(() => SpaceGoal.Find(progress, _goalCancel.Token));
            foreach (var i in _goalItems) i.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(GoalItem.Selected)) UpdateGoal(); };
            _goalShown = 25;
            ShowGoalList();
            GoalPanel.Visibility = Visibility.Visible;
            UpdateGoal();
        }
        catch (OperationCanceledException) { GoalStatus.Text = Loc.T("Cancelled. Nothing was changed."); }
        catch (Exception ex) { GoalStatus.Text = ex.Message; }
        finally
        {
            _goalBusy = false;
            GoalPlanBtn.IsEnabled = true;
            GoalCancelBtn.Visibility = Visibility.Collapsed;
        }
    }

    void ShowGoalList()
    {
        GoalList.ItemsSource = _goalItems.Take(_goalShown).ToList();
        int more = _goalItems.Count - _goalShown;
        GoalMoreBtn.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
        GoalMoreBtn.Content = Loc.F("Show {0} more", more);
    }

    void GoalMore_Click(object sender, RoutedEventArgs e) { _goalShown += 25; ShowGoalList(); }

    void GoalCancel_Click(object sender, RoutedEventArgs e) => _goalCancel?.Cancel();

    void GoalRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (_goalBusy) return;
        if ((sender as FrameworkElement)?.DataContext is GoalItem i) i.Selected = !i.Selected;
    }

    void GoalClear_Click(object sender, RoutedEventArgs e) { foreach (var i in _goalItems) i.Selected = false; }

    void UpdateGoal()
    {
        if (GoalProgress == null) return;
        long needed = SpaceGoal.Needed(GoalBytes, FreeNow);
        long picked = SpaceGoal.PickedBytes(_goalItems);
        long waiting = SpaceGoal.WaitingInBin(_goalItems);
        long available = _goalItems.Sum(i => i.Bytes);
        if (GoalPanel.Visibility == Visibility.Visible && !_goalBusy)
            GoalStatus.Text = needed == 0
                ? Loc.F("You already have {0} free.", Format.Bytes(FreeNow))
                : Loc.F("You have {0} free. To reach your goal, free {1} more. Pulse found {2} you could remove.", Format.Bytes(FreeNow), Format.Bytes(needed), Format.Bytes(available));
        double frac = needed <= 0 ? 1 : Math.Min(1, picked / (double)needed);
        GoalBar.Width = Math.Max(0, ((FrameworkElement)GoalBar.Parent).ActualWidth * frac);
        GoalProgress.Text = needed == 0 ? "" : picked >= needed
            ? Loc.F("Ticked {0}: enough to reach your goal.", Format.Bytes(picked))
            : Loc.F("Ticked {0} of the {1} needed. Tick {2} more.", Format.Bytes(picked), Format.Bytes(needed), Format.Bytes(needed - picked));
        GoalBinNote.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
        GoalBinNote.Text = Loc.F("{0} of your files go to the Recycle Bin, so they only free space once it's emptied. Tick the Recycle Bin too to count them.", Format.Bytes(waiting));
        int n = _goalItems.Count(i => i.Selected);
        GoalFreeBtn.IsEnabled = n > 0 && !_goalBusy;
        GoalFreeText.Text = n == 0 ? Loc.T("Free the ticked items") : Loc.F("Free {0} ({1} items)", Format.Bytes(picked + waiting), n);
    }

    async void GoalFree_Click(object sender, RoutedEventArgs e)
    {
        var picked = _goalItems.Where(i => i.Selected).ToList();
        if (picked.Count == 0 || _goalBusy) return;
        var lines = string.Join("\n", picked.Take(10).Select(i => $"•  {i.Title} ({i.SizeText}) · {i.KindText}"));
        if (picked.Count > 10) lines += "\n" + Loc.F("•  and {0} more", picked.Count - 10);
        bool programs = picked.Any(i => i.Kind == GoalKind.Program);
        if (!await MainWindow.Instance.Confirm(Loc.F("Free {0}?", Format.Bytes(SpaceGoal.PickedBytes(picked) + SpaceGoal.WaitingInBin(picked))),
                lines + "\n\n" + Loc.T("Your own files go to the Recycle Bin, so they can be restored until it's emptied.") +
                (programs ? " " + Loc.T("Programs open their own uninstaller, one at a time.") : "") + " " + Loc.T("You can cancel while it runs."), "Free space")) return;

        _goalBusy = true;
        _goalCancel = new CancellationTokenSource();
        long before = FreeNow;
        GoalFreeBtn.IsEnabled = false;
        GoalPlanBtn.IsEnabled = false;
        try
        {
            // The same progress screen as Free space: one line per item, percent done and time left, and a way to stop.
            var order = SpaceGoal.InOrder(picked);
            var steps = order.Select(i => new ProgressStep(i.Title)
            {
                Weight = i.Kind == GoalKind.Program ? 30 : i.Category != null ? 0.5 + i.Bytes / 300e6 : 1 + i.Bytes / 2e9,
            }).ToList();
            MainWindow.Instance.ShowSteps("Freeing space", "You can keep using your PC while this runs.", steps);
            MainWindow.Instance.AllowStepStop(() => _goalCancel?.Cancel());
            int done = 0, failed = 0;
            bool cancelled = false;
            for (int k = 0; k < order.Count; k++)
            {
                if (_goalCancel.IsCancellationRequested) { cancelled = true; foreach (var rest in steps.Skip(k)) rest.Skip(Loc.T("Stopped")); break; }
                var st = steps[k];
                st.Start();
                if (order[k].Kind == GoalKind.Program) st.Detail = Loc.T("Follow the uninstaller's window");
                bool ok;
                try { ok = (await Task.WhenAll(SpaceGoal.FreeOne(order[k]), Task.Delay(350).ContinueWith(_ => true)))[0]; }
                catch { ok = false; }
                if (ok) { done++; st.Finish(Loc.T("Done")); } else { failed++; st.Skip(Loc.T("In use or blocked")); }
            }
            long freed = Math.Max(0, FreeNow - before);
            TweakEngine.AddLog("clean", $"Free up to a goal: {done} items, {Format.Bytes(freed)} freed");
            long needed = SpaceGoal.Needed(GoalBytes, FreeNow);
            GoalStatus.Text = (cancelled ? Loc.T("Stopped. ") : "") +
                Loc.F("Freed {0}. You now have {1} free.", Format.Bytes(freed), Format.Bytes(FreeNow)) + " " +
                (needed == 0 ? Loc.T("Goal reached.") : Loc.F("{0} to go.", Format.Bytes(needed))) +
                (failed > 0 ? " " + Loc.F("{0} items couldn't be removed (in use or blocked).", failed) : "");
            await MainWindow.Instance.FinishSteps(freed > 0 ? Loc.F("Freed {0}", Format.Bytes(freed)) : Loc.T("Nothing could be removed right now"),
                GoalStatus.Text, ok: freed > 0 || failed == 0);
            _goalItems.RemoveAll(i => picked.Contains(i) && i.Kind != GoalKind.Program);
            foreach (var i in _goalItems) i.Selected = false;
            ShowGoalList();
        }
        catch (Exception ex) { GoalStatus.Text = ex.Message; }
        finally
        {
            _goalBusy = false;
            GoalPlanBtn.IsEnabled = true;
            GoalCancelBtn.Visibility = Visibility.Collapsed;
            UpdateGoal();
            _ = Scan();
        }
    }
}
