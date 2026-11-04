using System.Runtime.InteropServices;

namespace Pulse.Ui;

/// <summary>
/// Saves a clip when you say a phrase ("clip that"). Uses Windows' own speech engine (SAPI), which runs entirely on
/// the PC: nothing is sent anywhere and nothing is downloaded. It only listens for the one phrase.
/// </summary>
public sealed class VoiceClip : IDisposable
{
    dynamic _ctx, _grammar;
    readonly Action _onPhrase;
    Delegate _handler;
    DateTime _last;

    public string Phrase { get; }
    public string Error { get; private set; }

    /// <summary>The default phrase in the app's language.</summary>
    public static string DefaultPhrase => Loc.Current switch { "es" => "graba eso", "pt-BR" => "clipa isso", _ => "clip that" };

    VoiceClip(string phrase, Action onPhrase) { Phrase = phrase; _onPhrase = onPhrase; }

    /// <summary>Starts listening (call on the UI thread). Returns null with <paramref name="error"/> set if Windows has no speech engine.</summary>
    public static VoiceClip Start(string phrase, Action onPhrase, out string error)
    {
        var v = new VoiceClip(string.IsNullOrWhiteSpace(phrase) ? DefaultPhrase : phrase.Trim(), onPhrase);
        try
        {
            var type = Type.GetTypeFromProgID("SAPI.SpInProcRecoContext");
            if (type == null) { error = "Windows' speech engine isn't installed."; return null; }
            v._ctx = Activator.CreateInstance(type);
            dynamic recognizer = v._ctx.Recognizer;
            dynamic engines = recognizer.GetRecognizers();
            if ((int)engines.Count == 0) { error = "Windows has no speech recognizer for your language. Add one in Settings → Time & language → Speech."; v.Dispose(); return null; }
            recognizer.Recognizer = engines.Item(0);
            dynamic inputs = recognizer.GetAudioInputs();
            if ((int)inputs.Count == 0) { error = "No microphone found."; v.Dispose(); return null; }
            recognizer.AudioInput = inputs.Item(0);

            v._grammar = v._ctx.CreateGrammar(1);
            dynamic rule = v._grammar.Rules.Add("pulseclip", 1 | 32, 1); // top level, dynamic
            rule.InitialState.AddWordTransition(null, v.Phrase, " ", 1, "", 0, null, 1.0f);
            v._grammar.Rules.Commit();
            v._grammar.CmdSetRuleState("pulseclip", 1); // active

            // _ISpeechRecoContextEvents.Recognition (dispid 7)
            v._handler = new Action<int, object, int, object>((_, _, _, _) => v.Heard());
            ComEventsHelper.Combine((object)v._ctx, new Guid("7B8FCB42-0E9D-4F00-A048-7B04D6179D3D"), 7, v._handler);
            error = null;
            return v;
        }
        catch (Exception ex)
        {
            error = "Voice clipping couldn't start: " + ex.Message;
            v.Dispose();
            return null;
        }
    }

    void Heard()
    {
        // One clip per phrase, even if the engine reports it twice.
        if (DateTime.Now - _last < TimeSpan.FromSeconds(3)) return;
        _last = DateTime.Now;
        try { _onPhrase(); } catch { }
    }

    public void Dispose()
    {
        try { if (_grammar != null) _grammar.CmdSetRuleState("pulseclip", 0); } catch { }
        try { if (_handler != null) ComEventsHelper.Remove((object)_ctx, new Guid("7B8FCB42-0E9D-4F00-A048-7B04D6179D3D"), 7, _handler); } catch { }
        try { if (_ctx != null) Marshal.FinalReleaseComObject(_ctx); } catch { }
        _ctx = null; _grammar = null;
    }
}
