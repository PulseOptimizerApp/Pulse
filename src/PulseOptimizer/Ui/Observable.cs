using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Pulse.Ui;

/// <summary>Base class for anything the UI binds to.</summary>
public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    public void Raise([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Status colors shared by code and XAML (frozen, so safe to use from any thread).</summary>
public static class Palette
{
    static SolidColorBrush B(string hex, byte alpha = 255)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        c.A = alpha;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush Good = B("#34D399");
    public static readonly SolidColorBrush GoodSoft = B("#34D399", 0x26);
    public static readonly SolidColorBrush Warn = B("#FBBF24");
    public static readonly SolidColorBrush WarnSoft = B("#FBBF24", 0x26);
    public static readonly SolidColorBrush Bad = B("#F87171");
    public static readonly SolidColorBrush BadSoft = B("#F87171", 0x26);
    public static readonly SolidColorBrush Info = B("#60A5FA");
    public static readonly SolidColorBrush InfoSoft = B("#60A5FA", 0x26);
}
