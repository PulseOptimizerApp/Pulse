using Pulse.Core;
using Xunit;

namespace PulseOptimizer.Tests;

/// <summary>Wrong GPU or memory-slot classification would give people bad advice, so pin it down with real names.</summary>
public class DiagnosticsTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070")]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER")]
    [InlineData("AMD Radeon RX 7800 XT")]
    [InlineData("AMD Radeon RX 580 Series")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics")]
    [InlineData("NVIDIA RTX A4000")]
    public void Graphics_cards_are_discrete(string name)
    {
        Assert.True(PcDiagnostics.IsDiscrete(name), name);
        Assert.False(PcDiagnostics.IsIntegrated(name), name);
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics 770")]
    [InlineData("Intel(R) Iris(R) Xe Graphics")]
    [InlineData("Intel(R) HD Graphics 630")]
    [InlineData("AMD Radeon(TM) Graphics")]
    [InlineData("AMD Radeon Graphics")]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics")]
    [InlineData("Intel(R) Arc(TM) Graphics")]
    public void Built_in_graphics_are_integrated(string name)
    {
        Assert.True(PcDiagnostics.IsIntegrated(name), name);
        Assert.False(PcDiagnostics.IsDiscrete(name), name);
    }

    [Theory]
    [InlineData("DIMM_A1", "BANK 0", "A")]
    [InlineData("DIMM_B2", "BANK 3", "B")]
    [InlineData("ChannelA-DIMM0", "BANK 0", "A")]
    [InlineData("ChannelB-DIMM1", "BANK 2", "B")]
    [InlineData("Controller0-ChannelA-DIMM0", "BANK 0", "A")]
    [InlineData("A1_DIMM0", "P0 CHANNEL A", "A")]
    [InlineData("DIMM 0", "P0 CHANNEL B", "B")]
    [InlineData("DIMM1", "BANK 1", null)]
    public void Memory_channel_letter_is_read_from_slot_names(string locator, string bank, string expected) =>
        Assert.Equal(expected, PcDiagnostics.ChannelOf(locator, bank));
}
