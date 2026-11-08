using Microsoft.Win32;

namespace Pulse.Core.Audio;

/// <summary>
/// Pulse used to have a Mic studio and a Headphones page that could write settings for Equalizer APO. Both are gone,
/// so on start the files they wrote are emptied (kept, so Equalizer APO's config still loads without errors) and the
/// mic and headphones sound exactly as they did before Pulse.
/// </summary>
public static class OldAudioCleanup
{
    public static void Run()
    {
        try
        {
            var dir = Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\EqualizerAPO", "ConfigPath") as string;
            if (dir == null && Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\EqualizerAPO", "InstallPath") is string ip) dir = Path.Combine(ip, "config");
            if (dir == null || !Directory.Exists(dir)) return;
            foreach (var name in new[] { "pulse-mic.txt", "pulse-headphones.txt" })
            {
                var file = Path.Combine(dir, name);
                if (!File.Exists(file) || !File.ReadAllText(file).Contains("Device:", StringComparison.Ordinal)) continue;
                File.WriteAllText(file, "# Pulse no longer changes your sound. This file is left empty on purpose." + Environment.NewLine);
            }
        }
        catch { }
    }
}
