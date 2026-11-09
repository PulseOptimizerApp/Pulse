using System.Collections.Concurrent;
using System.Net.Http;

namespace Pulse.Core.Mouse;

/// <summary>
/// Product pictures for devices, the same ones OpenMouse shows: the picture is picked from the device's name by
/// OpenMouse's own table (bundled with the protocol library, see PulseMouse.image) and downloaded once from OpenMouse's
/// image server, then kept on disk. Only OpenMouse's servers are fetched from.
/// </summary>
public static class DeviceArt
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly ConcurrentDictionary<string, Task<string>> Pending = new();

    public static string Folder => Path.Combine(TweakEngine.DataDir, "device-art");

    /// <summary>The picture's file when it's already downloaded, else null.</summary>
    public static string Cached(string url)
    {
        var file = FileFor(url);
        return file != null && File.Exists(file) && new FileInfo(file).Length > 0 ? file : null;
    }

    /// <summary>The picture's file, downloading it first if needed. Null when there's no picture or it can't be had.</summary>
    public static Task<string> Get(string url) =>
        FileFor(url) == null ? Task.FromResult<string>(null) : Cached(url) is { } hit ? Task.FromResult(hit) : Pending.GetOrAdd(url, Fetch);

    static string FileFor(string url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host is not ("img.openmouse.app" or "openmouse.app")) return null;
        var name = System.IO.Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return null;
        return System.IO.Path.Combine(Folder, name);
    }

    static async Task<string> Fetch(string url)
    {
        try
        {
            var file = FileFor(url)!;
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            if (bytes.Length < 64 || bytes.Length > 8_000_000 || !LooksLikeImage(bytes)) throw new InvalidDataException("not a picture");
            Directory.CreateDirectory(Folder);
            var part = file + ".part";
            await File.WriteAllBytesAsync(part, bytes).ConfigureAwait(false);
            File.Move(part, file, overwrite: true);
            return file;
        }
        catch
        {
            // Try again next time (offline now, say).
            Pending.TryRemove(url, out _);
            return null;
        }
    }

    // PNG, JPEG or WebP.
    static bool LooksLikeImage(byte[] b) =>
        (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||
        (b[0] == 0xFF && b[1] == 0xD8) ||
        (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50);
}
