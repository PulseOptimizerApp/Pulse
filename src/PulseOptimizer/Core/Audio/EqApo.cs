using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace Pulse.Core.Audio;

/// <summary>
/// "No cable" mode: the sound is changed inside Windows itself by Equalizer APO (free, open source, signed), so
/// games keep playing to your real headset. Pulse writes Equalizer APO's settings for you: EQ, volume and crossfeed.
/// Pulse only writes its own file plus an Include line in config.txt (backed up first).
/// </summary>
public static class EqApo
{
    public const string Website = "https://sourceforge.net/projects/equalizerapo/";
    const string MicFile = "pulse-mic.txt", PhonesFile = "pulse-headphones.txt";

    public static string InstallPath => Read("InstallPath");
    public static string ConfigPath => Read("ConfigPath") ?? (InstallPath is { } p ? Path.Combine(p, "config") : null);
    public static bool Installed => ConfigPath is { } c && Directory.Exists(c);
    public static string Configurator => InstallPath is { } p && File.Exists(Path.Combine(p, "Configurator.exe")) ? Path.Combine(p, "Configurator.exe") : null;

    static string Read(string name)
    {
        try { return Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\EqualizerAPO", name) as string; }
        catch { return null; }
    }

    /// <summary>The GUID part of a Windows audio device id ("{0.0.1.00000000}.{guid}" → "{guid}").</summary>
    public static string Guid(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        int i = deviceId.LastIndexOf(".{", StringComparison.Ordinal);
        return i >= 0 ? deviceId[(i + 1)..] : deviceId;
    }

    /// <summary>Whether Equalizer APO was turned on for this device in its Configurator (null when unknown).</summary>
    public static bool? OnDevice(string deviceId)
    {
        var g = Guid(deviceId);
        if (g == null || !Installed) return null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO\Child APOs");
            if (k == null) return null;
            return k.GetSubKeyNames().Any(n => n.Equals(g, StringComparison.OrdinalIgnoreCase) || n.Contains(g.Trim('{', '}'), StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    public static void OpenConfigurator()
    {
        try { if (Configurator is { } c) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(c) { UseShellExecute = true }); } catch { }
    }

    /// <summary>A device's id and channel count: the chosen one, or Windows' default (mic: communications; output: multimedia).</summary>
    public static (string Id, int Channels) Resolve(string id, bool capture)
    {
        try
        {
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            NAudio.CoreAudioApi.MMDevice d = null;
            if (!string.IsNullOrEmpty(id)) try { d = en.GetDevice(id); } catch { }
            if (d == null || d.State != NAudio.CoreAudioApi.DeviceState.Active)
            {
                d = en.GetDefaultAudioEndpoint(capture ? NAudio.CoreAudioApi.DataFlow.Capture : NAudio.CoreAudioApi.DataFlow.Render,
                    capture ? NAudio.CoreAudioApi.Role.Communications : NAudio.CoreAudioApi.Role.Multimedia);
                // The default mic is often the cable itself: skip to a real mic.
                if (capture && Cables.IsVirtual(d.FriendlyName))
                {
                    var real = Cables.Devices().FirstOrDefault(x => x.Input && !Cables.IsVirtual(x.Name));
                    if (real.Id != null) d = en.GetDevice(real.Id);
                }
            }
            int ch = 2;
            try { ch = d.AudioClient.MixFormat.Channels; } catch { }
            return (d.ID, ch);
        }
        catch { return (id, 2); }
    }

    static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Adds "Include: pulse-headphones.txt" to config.txt once (keeping a copy of the original).</summary>
    static void EnsureIncludes(string dir)
    {
        var cfg = Path.Combine(dir, "config.txt");
        var text = File.Exists(cfg) ? File.ReadAllText(cfg) : "";
        if (text.Contains("Include: " + PhonesFile, StringComparison.OrdinalIgnoreCase)) return;
        var backup = Path.Combine(dir, "config.before-pulse.txt");
        if (!File.Exists(backup)) File.WriteAllText(backup, text);
        File.WriteAllText(cfg, text.TrimEnd() + Environment.NewLine + Environment.NewLine +
            "# Pulse: your headphone settings (written by the Pulse app)" + Environment.NewLine +
            "Include: " + PhonesFile + Environment.NewLine);
    }

    static void Write(string file, string body)
    {
        var dir = ConfigPath;
        if (dir == null) return;
        EnsureIncludes(dir);
        File.WriteAllText(Path.Combine(dir, file), "# Written by Pulse. Change these settings in Pulse; edits here are replaced." + Environment.NewLine + body);
    }

    internal static string PhonesConfig(GameAudioSettings s, string deviceId)
    {
        var g = Guid(deviceId);
        if (!s.Enabled || g == null) return "";
        var b = new StringBuilder();
        b.AppendLine("Device: " + g);
        double boost = Math.Max(0, s.Eq?.Max() ?? 0);
        b.AppendLine($"Preamp: {F(s.VolumeDb - boost)} dB");
        Tone(b, 0, 0, 0, s.Eq);
        // Speakers in front of you: each ear hears a little of the other side, slightly later (stereo crossfeed).
        if (s.Spatial) b.AppendLine("Copy: L=0.82*L+0.28*R R=0.82*R+0.28*L");
        b.AppendLine("Device: all");
        return b.ToString();
    }

    static void Tone(StringBuilder b, double bass, double clarity, double air, double[] eq)
    {
        if (Math.Abs(bass) > 0.05) b.AppendLine($"Filter: ON LS Fc 160 Hz Gain {F(bass)} dB");
        if (Math.Abs(clarity) > 0.05) b.AppendLine($"Filter: ON PK Fc 3200 Hz Gain {F(clarity)} dB Q 0.9");
        if (Math.Abs(air) > 0.05) b.AppendLine($"Filter: ON HS Fc 9000 Hz Gain {F(air)} dB");
        if (eq == null) return;
        for (int i = 0; i < Math.Min(10, eq.Length); i++)
        {
            if (Math.Abs(eq[i]) < 0.05) continue;
            b.AppendLine(i switch
            {
                0 => $"Filter: ON LS Fc 40 Hz Gain {F(eq[i])} dB",
                9 => $"Filter: ON HS Fc 12000 Hz Gain {F(eq[i])} dB",
                _ => $"Filter: ON PK Fc {F(EqBands.Freqs[i])} Hz Gain {F(eq[i])} dB Q 1.4",
            });
        }
    }

    /// <summary>
    /// Pulse used to have a Mic studio that could also write mic settings here. It's gone, so an older install's mic
    /// settings are emptied (the file stays, so Equalizer APO's config keeps loading without errors).
    /// </summary>
    public static void ClearMic()
    {
        try
        {
            if (ConfigPath is not { } dir) return;
            var file = Path.Combine(dir, MicFile);
            if (!File.Exists(file) || !File.ReadAllText(file).Contains("Device:", StringComparison.Ordinal)) return;
            File.WriteAllText(file, "# Pulse no longer changes your mic. This file is left empty on purpose." + Environment.NewLine);
        }
        catch { }
    }

    public static string ApplyPhones(GameAudioSettings s, string deviceId)
    {
        if (!Installed) return "Equalizer APO isn't installed.";
        try { Write(PhonesFile, PhonesConfig(s, deviceId)); return null; }
        catch (Exception ex) { return ex.Message; }
    }
}
