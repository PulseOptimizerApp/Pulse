namespace Pulse.Ui;

public enum StepState { Waiting, Active, Done, Skipped }

/// <summary>One line in the step-by-step progress screen (see MainWindow.ShowSteps).</summary>
public class ProgressStep : Observable
{
    public ProgressStep(string name) => Name = Loc.T(name);

    public string Name { get; }

    StepState _state;
    public StepState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsWaiting)); Raise(nameof(IsActive)); Raise(nameof(IsDone)); Raise(nameof(IsSkipped));
        }
    }

    public bool IsWaiting => _state == StepState.Waiting;
    public bool IsActive => _state == StepState.Active;
    public bool IsDone => _state == StepState.Done;
    public bool IsSkipped => _state == StepState.Skipped;

    string _detail = "";
    /// <summary>Shown at the right of the line, e.g. "32.6 MB freed". Already translated by the caller.</summary>
    public string Detail { get => _detail; set => Set(ref _detail, value ?? ""); }

    /// <summary>How long this step is expected to take compared with the others (seconds, roughly). Drives the overall bar and time left.</summary>
    public double Weight { get; set; } = 1;

    double _fraction;
    /// <summary>How far this step is (0 to 1) while it runs, when it can tell.</summary>
    public double Fraction { get => _fraction; set => Set(ref _fraction, Math.Clamp(value, 0, 1)); }

    public void Start() { State = StepState.Active; Detail = Loc.T("Working…"); }
    public void Finish(string detail) { Detail = detail; State = StepState.Done; }
    public void Skip(string detail) { Detail = detail; State = StepState.Skipped; }
}
