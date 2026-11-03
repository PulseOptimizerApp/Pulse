using System.Text.Json;

namespace Pulse.Core;

public enum GpuVendor { Nvidia, Amd, Intel, Unknown }
public enum BoardMaker { Asus, Msi, Gigabyte, AsRock, Prebuilt, Unknown }

/// <summary>Facts used to tailor fix steps: which menus, which keys, which app.</summary>
public class HardwareFacts
{
    public GpuVendor Gpu { get; init; }
    public bool AmdCpu { get; init; }
    public BoardMaker Board { get; init; }
    public string BoardText { get; init; } = "";
    public bool Laptop { get; init; }
    public bool Windows11 { get; init; }

    /// <summary>"XMP" on Intel platforms, "EXPO" (or XMP/DOCP on older boards) on AMD.</summary>
    public string MemoryProfile => AmdCpu ? "EXPO (or DOCP / XMP on older boards)" : "XMP";

    public string BiosKey => Board switch
    {
        BoardMaker.AsRock => "F2 or Delete",
        BoardMaker.Prebuilt => "F2 (Dell, Lenovo, Acer), F10 (HP) or Delete",
        _ => "Delete",
    };

    static HardwareFacts _cached;

    public static HardwareFacts Get()
    {
        if (_cached != null) return _cached;
        string boardMaker = "", product = "", sysMaker = "";
        try
        {
            var r = Shell.PowerShell("$b = Get-CimInstance Win32_BaseBoard; $c = Get-CimInstance Win32_ComputerSystem; ConvertTo-Json -Compress -InputObject @{ B = [string]$b.Manufacturer; P = [string]$b.Product; S = [string]$c.Manufacturer }", 30_000);
            using var doc = JsonDocument.Parse(r.Output);
            boardMaker = doc.RootElement.GetProperty("B").GetString() ?? "";
            product = doc.RootElement.GetProperty("P").GetString() ?? "";
            sysMaker = doc.RootElement.GetProperty("S").GetString() ?? "";
        }
        catch { }

        static bool Has(string s, string x) => s.Contains(x, StringComparison.OrdinalIgnoreCase);
        var board =
            Has(boardMaker, "ASUS") ? BoardMaker.Asus :
            Has(boardMaker, "Micro-Star") || Has(boardMaker, "MSI") ? BoardMaker.Msi :
            Has(boardMaker, "Gigabyte") ? BoardMaker.Gigabyte :
            Has(boardMaker, "ASRock") ? BoardMaker.AsRock :
            new[] { "Dell", "HP", "Hewlett", "Lenovo", "Acer", "Alienware", "Microsoft", "Samsung", "Razer" }.Any(x => Has(sysMaker, x) || Has(boardMaker, x)) ? BoardMaker.Prebuilt :
            BoardMaker.Unknown;

        var gpus = SystemInfo.Gpus.Select(g => g.Name).ToList();
        var gpu =
            gpus.Any(g => Has(g, "NVIDIA") || Has(g, "GeForce")) ? GpuVendor.Nvidia :
            gpus.Any(g => Has(g, "Radeon RX") || Has(g, "Radeon Pro")) ? GpuVendor.Amd :
            gpus.Any(g => Has(g, "Arc")) ? GpuVendor.Intel :
            gpus.Any(g => Has(g, "Radeon")) ? GpuVendor.Amd :
            gpus.Any(g => Has(g, "Intel")) ? GpuVendor.Intel : GpuVendor.Unknown;

        return _cached = new HardwareFacts
        {
            Gpu = gpu,
            AmdCpu = Has(SystemInfo.Cpu, "AMD") || Has(SystemInfo.Cpu, "Ryzen"),
            Board = board,
            BoardText = $"{boardMaker} {product}".Trim(),
            Laptop = SystemInfo.HasBattery,
            Windows11 = SystemInfo.IsWindows11,
        };
    }
}
