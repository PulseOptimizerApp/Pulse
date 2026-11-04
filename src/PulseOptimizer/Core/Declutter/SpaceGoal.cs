using Pulse.Ui;

namespace Pulse.Core.Declutter;

/// <summary>How an item frees space, which also says how safe it is.</summary>
public enum GoalKind
{
    /// <summary>Temporary files and caches that Windows or the app makes again: deleted.</summary>
    Safe,
    /// <summary>The user's own files: moved to the Recycle Bin, so they can be restored.</summary>
    YourFiles,
    /// <summary>A program or game: its own uninstaller runs (or the Store app is removed).</summary>
    Program,
    /// <summary>The Recycle Bin itself: emptying it is what actually frees the space of recycled files.</summary>
    RecycleBin,
}

/// <summary>One thing in the plan. Nothing is picked until the user picks it.</summary>
public class GoalItem : Observable
{
    public GoalKind Kind { get; init; }
    public string Title { get; init; }
    public string Detail { get; init; } = "";
    public long Bytes { get; init; }
    public string SizeText => Format.Bytes(Bytes);
    public string KindText => Loc.T(Kind switch
    {
        GoalKind.Safe => "Safe to remove",
        GoalKind.YourFiles => "Your file: goes to the Recycle Bin",
        GoalKind.Program => "Program: opens its uninstaller",
        _ => "Empties the Recycle Bin",
    });

    bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    // What to run when the user confirms (exactly one is set).
    public CleanCategory Category { get; init; }
    public ReviewFile File { get; init; }
    public AppEntry App { get; init; }
}

/// <summary>
/// "I want X GB free": finds what's taking the space, ranked biggest first, and frees only what the user ticks,
/// after they confirm. Scanning and freeing can both be cancelled at any point.
/// </summary>
public static class SpaceGoal
{
    /// <summary>Bytes still to free to reach the goal (0 when it's already met).</summary>
    public static long Needed(long goalFreeBytes, long freeNow) => Math.Max(0, goalFreeBytes - freeNow);

    /// <summary>
    /// How much the picked items will free. Files sent to the Recycle Bin only free space once it's emptied, so they
    /// count only when the Recycle Bin is picked too.
    /// </summary>
    public static long PickedBytes(IEnumerable<GoalItem> items)
    {
        var picked = items.Where(i => i.Selected).ToList();
        bool emptying = picked.Any(i => i.Kind == GoalKind.RecycleBin);
        return picked.Where(i => i.Kind != GoalKind.YourFiles || emptying).Sum(i => i.Bytes);
    }

    /// <summary>Bytes picked as "your files" that won't count until the Recycle Bin is emptied.</summary>
    public static long WaitingInBin(IEnumerable<GoalItem> items)
    {
        var picked = items.Where(i => i.Selected).ToList();
        return picked.Any(i => i.Kind == GoalKind.RecycleBin) ? 0 : picked.Where(i => i.Kind == GoalKind.YourFiles).Sum(i => i.Bytes);
    }

    /// <summary>Everything that could free space on the system drive, biggest first. Cancellable.</summary>
    public static List<GoalItem> Find(IProgress<string> progress, CancellationToken cancel)
    {
        var items = new List<GoalItem>();

        progress?.Report(Loc.T("Checking temporary files and caches…"));
        foreach (var c in Cleaner.Categories)
        {
            cancel.ThrowIfCancellationRequested();
            var copy = c; // Quick clean's own ticks aren't touched
            try { Cleaner.Scan(copy); } catch { continue; }
            // The Recycle Bin is always offered: emptying it is what frees the space of files sent there.
            if (copy.Size < 50L << 20 && !copy.IsRecycleBin) continue;
            items.Add(new GoalItem
            {
                Kind = copy.IsRecycleBin ? GoalKind.RecycleBin : GoalKind.Safe,
                Title = Loc.T(copy.Name), Detail = Loc.T(copy.Warning.Length > 0 ? copy.Warning : copy.Description), Bytes = copy.Size, Category = copy,
            });
        }

        cancel.ThrowIfCancellationRequested();
        progress?.Report(Loc.T("Looking for big files you haven't opened in a while…"));
        try
        {
            foreach (var f in DeclutterEngine.OldInstallers().Concat(DeclutterEngine.LargeOldFiles(cancel)))
                items.Add(new GoalItem { Kind = GoalKind.YourFiles, Title = f.Name, Detail = f.Folder + " · " + Loc.T(f.AgeText), Bytes = f.Size, File = f });
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        cancel.ThrowIfCancellationRequested();
        progress?.Report(Loc.T("Measuring games and programs…"));
        try
        {
            foreach (var a in DeclutterEngine.Apps().Where(a => a.Size >= 1L << 30 && a.Advice != AppAdvice.Keep))
            {
                cancel.ThrowIfCancellationRequested();
                items.Add(new GoalItem { Kind = GoalKind.Program, Title = a.Name, Detail = Loc.T(a.Kind == "Game" ? "Game" : "Program") + (a.Why.Length > 0 ? " · " + Loc.T(a.Why) : ""), Bytes = a.Size, App = a });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // Biggest first, with the Recycle Bin pinned to the top so it's easy to tick alongside your files.
        return items.OrderByDescending(i => i.Kind == GoalKind.RecycleBin).ThenByDescending(i => i.Bytes).ToList();
    }

    /// <summary>
    /// Frees the picked items: safe items and your files first, programs next, the Recycle Bin last. Stops between
    /// items when cancelled. Returns what was done.
    /// </summary>
    /// <summary>The order things are removed in: quick safe clean-ups first, then your files, then programs.</summary>
    public static List<GoalItem> InOrder(IReadOnlyList<GoalItem> picked) =>
        picked.OrderBy(i => i.Kind switch { GoalKind.Safe => 0, GoalKind.YourFiles => 1, GoalKind.Program => 2, _ => 3 }).ToList();

    /// <summary>Removes one item. True when it went.</summary>
    public static async Task<bool> FreeOne(GoalItem i)
    {
        if (i.Category != null) return await Task.Run(() => Cleaner.Clean(i.Category)) > 0 || i.Bytes == 0;
        if (i.File != null) return await DeclutterEngine.RecycleAsync(new[] { i.File }) > 0;
        if (i.App != null)
            return (i.App.IsStoreApp ? await Task.Run(() => DeclutterEngine.RemoveStoreApp(i.App)) : await Task.Run(() => DeclutterEngine.StartUninstaller(i.App))).ok;
        return false;
    }

    public static async Task<(int Done, int Failed, bool Cancelled)> Free(IReadOnlyList<GoalItem> picked, IProgress<string> progress, CancellationToken cancel)
    {
        int done = 0, failed = 0;
        foreach (var i in InOrder(picked))
        {
            if (cancel.IsCancellationRequested) return (done, failed, true);
            progress?.Report(i.Title);
            try { if (await FreeOne(i)) done++; else failed++; }
            catch { failed++; }
        }
        return (done, failed, false);
    }
}
