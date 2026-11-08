using Pulse.Core.Audio;
using Xunit;

namespace PulseOptimizer.Tests;

public class EqApoTests
{
    const string Id = "{0.0.1.00000000}.{4c1f2a3b-1111-2222-3333-444455556666}";

    [Fact]
    public void Device_ids_become_the_guid_Equalizer_APO_matches() =>
        Assert.Equal("{4c1f2a3b-1111-2222-3333-444455556666}", EqApo.Guid(Id));

    [Fact]
    public void Off_means_no_change()
    {
        Assert.Equal("", EqApo.PhonesConfig(new GameAudioSettings { Enabled = false }, Id));
    }

    [Fact]
    public void Headphone_presets_keep_headroom()
    {
        var s = new GameAudioSettings { Enabled = true };
        s.ApplyPreset("footsteps");
        var cfg = EqApo.PhonesConfig(s, Id);
        Assert.Contains("Preamp: -5 dB", cfg); // the biggest boost is +5 dB at 4 kHz
        Assert.Contains("Filter: ON PK Fc 4000 Hz Gain 5 dB Q 1.4", cfg);
    }
}

public class CableTests
{
    [Fact]
    public void Newer_vb_cable_and_voicemeeter_names_are_cables()
    {
        Assert.True(Cables.IsVirtual("CABLE In 16ch (VB-Audio Virtual Cable)"));
        Assert.True(Cables.IsVirtual("Voicemeeter In 3 (VB-Audio Voicemeeter VAIO)"));
        Assert.False(Cables.IsVirtual("Microphone (RIG-M2 PRO HD)"));
    }

    [Fact]
    public void Ends_pair_up_correctly()
    {
        Assert.True(Cables.SameCable("CABLE In 16ch (VB-Audio Virtual Cable)", "CABLE Output (VB-Audio Virtual Cable)"));
        Assert.True(Cables.SameCable("Voicemeeter In 3 (VB-Audio Voicemeeter VAIO)", "Voicemeeter Out 3 (VB-Audio Voicemeeter VAIO)"));
        Assert.False(Cables.SameCable("Voicemeeter In 3 (VB-Audio Voicemeeter VAIO)", "Voicemeeter Out 1 (VB-Audio Voicemeeter VAIO)"));
        Assert.False(Cables.SameCable("CABLE In 16ch (VB-Audio Virtual Cable)", "Hi-Fi Cable Output (VB-Audio Hi-Fi Cable)"));
    }
}
