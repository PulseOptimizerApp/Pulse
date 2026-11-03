using System.Windows.Media;
using Pulse.Ui;

namespace Pulse.Core;

public enum Risk { Safe, Moderate, Advanced }

public enum TweakCategory { Gaming, Performance, Network, Input, Privacy, Visual, Storage }

/// <summary>How well-backed a tweak is (see the research doc).</summary>
public enum Evidence
{
    /// <summary>Setting and meaning are documented by Microsoft.</summary>
    Documented,
    /// <summary>Real setting, but only helps on some hardware or in some games. Offered only when its gate is met.</summary>
    Gated,
    /// <summary>Comfort/privacy setting. No performance claim until a Process Monitor trace proves Windows reads it.</summary>
    NoPerformanceClaim,
}

/// <summary>What the measurement module uses to judge a tweak.</summary>
public enum ProofMetric { None, FrameTime, Latency, Polling, DiskActivity, BackgroundCpu, FreeSpace }

public record GateResult(bool Met, string Why);

/// <summary>Everything needed to put one setting back exactly how it was.</summary>
public class ActionBackup
{
    public string Kind { get; set; }
    public Dictionary<string, string> Data { get; set; } = new();
    public List<ActionBackup> Children { get; set; } = new();
}

/// <summary>One concrete change a tweak makes (a registry value, a command, a service...).</summary>
public abstract class TweakAction
{
    /// <summary>True if the system already has this change, false if not, null if unknown.</summary>
    public abstract bool? Check();
    public abstract ActionBackup Apply();
    /// <summary>Undo. If no backup exists (the setting was changed before Pulse), restore Windows' default.</summary>
    public abstract void Revert(ActionBackup backup);
}

public class Tweak : Observable
{
    public string Id { get; init; }
    public string Title { get; init; }
    /// <summary>One-line plain-English summary shown on the card.</summary>
    public string Summary { get; init; }
    /// <summary>"What it does" — longer explanation.</summary>
    public string Explain { get; init; }
    /// <summary>The exact setting it touches.</summary>
    public string Changes { get; init; }
    /// <summary>What the evidence says about the benefit — never a promise.</summary>
    public string Gain { get; init; }
    /// <summary>Downsides / when not to use it.</summary>
    public string Tradeoff { get; init; }
    public TweakCategory Category { get; init; }
    public Risk Risk { get; init; }
    public Evidence Evidence { get; init; }
    public ProofMetric Proof { get; init; } = ProofMetric.None;
    /// <summary>Primary source backing the setting (Microsoft docs where possible).</summary>
    public string SourceName { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    /// <summary>Recommended when its gate is met.</summary>
    public bool Recommended { get; init; }
    /// <summary>"Restart needed", "Sign out needed" or empty.</summary>
    public string RestartNote { get; init; } = "";
    public List<TweakAction> Actions { get; init; } = new();
    /// <summary>Decides whether this PC should be offered the tweak. Null = always.</summary>
    public Func<GateResult> Gate { get; init; }
    /// <summary>Runs after apply and after undo.</summary>
    public Action AfterChange { get; init; }

    bool _isOn, _isBusy;
    public bool IsOn { get => _isOn; set { if (Set(ref _isOn, value)) Raise(nameof(StatusText)); } }
    /// <summary>"On" plus who set it, so a tweak Windows already had doesn't look like Pulse's doing.</summary>
    public string StatusText => !IsOn ? "" : TweakEngine.AppliedByPulse(this) ? "On · set by Pulse" : "Already set on this PC";
    public void RefreshStatus() => Raise(nameof(StatusText));
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    GateResult _gate;
    /// <summary>Evaluated once per session (gates query hardware).</summary>
    public GateResult GateStatus => _gate ??= EvalGate();
    GateResult EvalGate()
    {
        try { return Gate?.Invoke() ?? new GateResult(true, ""); }
        catch (Exception ex) { return new GateResult(false, "Couldn't check this PC: " + ex.Message); }
    }

    /// <summary>Latest measured verdict on this PC, if the user ran a test.</summary>
    string _measured = "";
    public string MeasuredResult { get => _measured; set { if (Set(ref _measured, value)) Raise(nameof(HasMeasuredResult)); } }
    public bool HasMeasuredResult => !string.IsNullOrEmpty(MeasuredResult);

    public bool MakesPerformanceClaim => Evidence != Evidence.NoPerformanceClaim && Proof is ProofMetric.FrameTime or ProofMetric.Latency or ProofMetric.Polling or ProofMetric.DiskActivity or ProofMetric.BackgroundCpu;
    public bool HasRestartNote => !string.IsNullOrEmpty(RestartNote);
    public bool HasSource => !string.IsNullOrEmpty(SourceUrl);

    public string CategoryText => Category switch
    {
        TweakCategory.Privacy => "Privacy & Security",
        TweakCategory.Input => "Input & Controllers",
        _ => Category.ToString()
    };

    public string EvidenceText => Evidence switch
    {
        Evidence.Documented => "Documented by Microsoft",
        Evidence.Gated => "Helps only on some PCs",
        _ => "Comfort / privacy — no speed claim",
    };

    public string RiskText => Risk.ToString();

    public Brush RiskBrush => Risk switch
    {
        Risk.Safe => Palette.Good,
        Risk.Moderate => Palette.Warn,
        _ => Palette.Bad
    };

    public Brush RiskBackground => Risk switch
    {
        Risk.Safe => Palette.GoodSoft,
        Risk.Moderate => Palette.WarnSoft,
        _ => Palette.BadSoft
    };
}
