using Microsoft.Win32;
using Pulse.Core;
using Xunit;

namespace PulseOptimizer.Tests;

/// <summary>
/// "Exact undo": applying then reverting a change must leave the value byte-for-byte as it was,
/// including when it didn't exist before. Runs in a throwaway key under HKCU.
/// </summary>
public class UndoTests : IDisposable
{
    const string Key = @"Software\PulseOptimizerTests";

    public UndoTests() => Registry.CurrentUser.DeleteSubKeyTree(Key, false);
    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(Key, false);

    static object Get(string name) => Registry.CurrentUser.OpenSubKey(Key)?.GetValue(name);

    [Fact]
    public void Missing_value_is_removed_again_on_undo()
    {
        var a = new RegAction(RegistryHive.CurrentUser, Key, "Missing", 1);
        var backup = a.Apply();
        Assert.Equal(1, Get("Missing"));
        a.Revert(backup);
        Assert.Null(Get("Missing"));
    }

    [Theory]
    [InlineData(RegistryValueKind.DWord, 20, 10)]
    [InlineData(RegistryValueKind.String, "400", "20")]
    [InlineData(RegistryValueKind.QWord, 5L, 7L)]
    public void Existing_value_is_restored_exactly(RegistryValueKind kind, object original, object changed)
    {
        Registry.CurrentUser.CreateSubKey(Key).SetValue("V", original, kind);
        var a = new RegAction(RegistryHive.CurrentUser, Key, "V", changed, kind);
        var backup = a.Apply();
        Assert.Equal(changed, Get("V"));
        a.Revert(backup);
        Assert.Equal(original, Get("V"));
        Assert.Equal(kind, Registry.CurrentUser.OpenSubKey(Key).GetValueKind("V"));
    }

    [Fact]
    public void DWord_FFFFFFFF_round_trips()
    {
        Registry.CurrentUser.CreateSubKey(Key).SetValue("Big", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
        var a = new RegAction(RegistryHive.CurrentUser, Key, "Big", 10);
        var b = a.Apply();
        a.Revert(b);
        Assert.Equal(unchecked((int)0xFFFFFFFF), Get("Big"));
    }

    [Fact]
    public void Binary_value_round_trips()
    {
        var bytes = new byte[] { 3, 0, 0, 0, 9, 8, 7, 6, 5, 4, 3, 2 };
        Registry.CurrentUser.CreateSubKey(Key).SetValue("Bin", bytes, RegistryValueKind.Binary);
        var a = new RegAction(RegistryHive.CurrentUser, Key, "Bin", new byte[] { 2 }, RegistryValueKind.Binary);
        var b = a.Apply();
        a.Revert(b);
        Assert.Equal(bytes, (byte[])Get("Bin"));
    }

    [Fact]
    public void Check_reports_applied_state()
    {
        var a = new RegAction(RegistryHive.CurrentUser, Key, "C", 1);
        Assert.False(a.Check());
        var b = a.Apply();
        Assert.True(a.Check());
        a.Revert(b);
        Assert.False(a.Check());
    }
}
