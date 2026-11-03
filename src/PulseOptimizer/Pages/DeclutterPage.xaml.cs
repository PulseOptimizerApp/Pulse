using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
        FreeableSmall.Text = "  ready to free";
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
        int i = 0;
        foreach (var s in parts)
        {
            MapBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(s.Bytes, 1), GridUnitType.Star) });
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
        long before = SystemInfo.SystemDrive.AvailableFreeSpace;
        long recycled = 0;
        MainWindow.Instance.ShowBusy("Freeing up space", "You can keep using your PC while this runs.");
        try
        {
            foreach (var c in cats)
            {
                MainWindow.Instance.SetBusyStatus(c.Name + "…");
                await Task.Run(() => Cleaner.Clean(c.Category));
            }
            if (files.Count > 0)
            {
                MainWindow.Instance.SetBusyStatus("Moving files to the Recycle Bin…");
                recycled = await DeclutterEngine.RecycleAsync(files);
            }
            if (hib != null)
            {
                MainWindow.Instance.HideOverlay();
                var t = TweakCatalog.All.First(x => x.Id == "hibernate");
                await TweakRunner.Set(t, true);
                MainWindow.Instance.ShowBusy("Freeing up space", "You can keep using your PC while this runs.");
            }
            string dismNote = "";
            if (dism != null)
            {
                MainWindow.Instance.SetBusyStatus("Cleaning old Windows update files… this is the slow part.");
                var (ok, _, msg) = await Task.Run(DeclutterEngine.CleanWindowsUpdateFiles);
                if (!ok) dismNote = " Windows' update cleanup didn't finish: " + msg;
            }
            MainWindow.Instance.HideOverlay();
            long freed = Math.Max(SystemInfo.SystemDrive.AvailableFreeSpace - before, 0);
            TweakEngine.AddLog("clean", $"Declutter freed {Format.Bytes(freed)}");
            var binNote = recycled > 0 ? $" {Format.Bytes(recycled)} is in the Recycle Bin; empty it when you're sure to get that space back too." : "";
            await MainWindow.Instance.Alert($"Freed {Format.Bytes(freed)}", "Done." + binNote + dismNote);
        }
        catch (Exception ex)
        {
            MainWindow.Instance.HideOverlay();
            await MainWindow.Instance.Alert("Cleanup stopped", ex.Message);
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
            _ => _apps.OrderByDescending(a => a.Size),
        };
        if (q.Length > 0) list = _apps.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)).OrderByDescending(a => a.Size);
        return list;
    }

    void ShowApps()
    {
        if (AppList == null) return;
        var all = FilteredApps().ToList();
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
        var text = app.IsStoreApp
            ? $"{(app.Why.Length > 0 ? app.Why + ". " : "")}It's removed for your account and can be reinstalled from the Microsoft Store any time."
            : $"Pulse runs {app.Name}'s own uninstaller{(app.Wait ? "; follow its steps if it shows a window" : " through Steam")}.";
        if (!await MainWindow.Instance.Confirm($"Remove {app.Name}?", text, "Remove")) return;

        app.Busy = true;
        try
        {
            var (ok, msg) = await Task.Run(() => app.IsStoreApp ? DeclutterEngine.RemoveStoreApp(app) : DeclutterEngine.StartUninstaller(app));
            if (!ok) { await MainWindow.Instance.Alert($"Couldn't remove {app.Name}", msg); return; }
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
}
