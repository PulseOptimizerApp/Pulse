namespace Pulse.Pages;

static class UiExt
{
    /// <summary>Set something up inline: <c>new TextBlock().Also(t => t.Margin = ...)</c>.</summary>
    public static T Also<T>(this T x, Action<T> a) { a(x); return x; }
}
