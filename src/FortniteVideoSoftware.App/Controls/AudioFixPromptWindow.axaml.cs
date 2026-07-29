using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.Core.Media;
using System;
using System.Threading.Tasks;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// The answer the user gave to an audio warning.
/// </summary>
/// <param name="Apply">Whether to fix THIS video.</param>
/// <param name="Remember">
/// What to remember for next time. <see cref="AudioFixPrompt.Ask"/> means "ask me again",
/// i.e. neither checkbox was ticked.
/// </param>
public readonly record struct AudioFixDecision(bool Apply, AudioFixPrompt Remember);

/// <summary>
/// Shared warning dialog for both audio problems the app can detect on upload:
///
///   1. LOUDNESS — the recording's average volume sits outside the band viewers expect, so the
///      audience has to reach for their volume control.
///   2. HARSH PEAKS — the recording is fine on average but hides a sudden bang far above it,
///      which is the thing that actually hurts somebody wearing headphones.
///
/// One window serves both because the shape of the question is identical (here is what is wrong,
/// shall I fix it, and should I remember your answer). Only the wording differs, and that lives
/// in the two factory methods below so the two messages cannot drift apart in style.
/// </summary>
public partial class AudioFixPromptWindow : Window
{
    private CheckBox? _alwaysCheck;
    private CheckBox? _neverCheck;
    private bool _suppressCheckSync;

    /// <summary>The user's answer. Defaults to "don't fix, don't remember" so a force-closed
    /// dialog can never silently opt the user into changing their audio.</summary>
    public AudioFixDecision Decision { get; private set; } = new(false, AudioFixPrompt.Ask);

    public AudioFixPromptWindow()
    {
        InitializeComponent();

        _alwaysCheck = this.FindControl<CheckBox>("AlwaysCheck");
        _neverCheck = this.FindControl<CheckBox>("NeverCheck");

        if (_alwaysCheck != null)
            _alwaysCheck.IsCheckedChanged += (_, _) =>
            {
                if (_suppressCheckSync) return;
                if (_alwaysCheck.IsChecked == true)
                {
                    _suppressCheckSync = true;
                    if (_neverCheck != null) _neverCheck.IsChecked = false;
                    _suppressCheckSync = false;
                }
            };

        if (_neverCheck != null)
            _neverCheck.IsCheckedChanged += (_, _) =>
            {
                if (_suppressCheckSync) return;
                if (_neverCheck.IsChecked == true)
                {
                    _suppressCheckSync = true;
                    if (_alwaysCheck != null) _alwaysCheck.IsChecked = false;
                    _suppressCheckSync = false;
                }
            };

        var yes = this.FindControl<Button>("YesBtn");
        if (yes != null) yes.Click += (_, _) => Finish(apply: true);

        var no = this.FindControl<Button>("NoBtn");
        if (no != null) no.Click += (_, _) => Finish(apply: false);

        AttachTitleBarDrag();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Finish(bool apply)
    {
        AudioFixPrompt remember = AudioFixPrompt.Ask;
        if (apply && _alwaysCheck?.IsChecked == true) remember = AudioFixPrompt.AlwaysApply;
        else if (!apply && _neverCheck?.IsChecked == true) remember = AudioFixPrompt.NeverApply;

        Decision = new AudioFixDecision(apply, remember);
        Close();
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<TextBlock>("DialogTitle");
        if (titleBar?.Parent is Border border)
        {
            border.PointerPressed += (_, e) =>
            {
                if (e.ClickCount < 2) BeginMoveDrag(e);
            };
        }
    }

    /// <summary>
    /// Builds the LOUDNESS warning — the recording is quieter or louder than viewers expect.
    /// </summary>
    public static AudioFixPromptWindow ForLoudness(LoudnessReading reading)
    {
        var win = new AudioFixPromptWindow();
        bool quiet = reading.Verdict == LoudnessVerdict.TooQuiet;
        string direction = quiet ? "lower" : "higher";
        double off = Math.Abs(reading.GainToStandardDb);

        win.Title = "Warning — Video Volume";
        win.SetText(
            title: "Warning — Video Volume",
            message: $"The uploaded video contains {direction} volume than normal industry standards.",
            question: quiet
                ? "Would you like to normalize the audio so that it is industry standard volume?"
                : "Would you like to normalize the audio so that it is industry standard volume?",
            detail: $"Measured average loudness: {reading.IntegratedLufs:F1} LUFS. " +
                    $"The standard used by YouTube, TikTok and Instagram is about {AudioLoudnessProbe.TargetLufs:F0} LUFS — " +
                    $"your video is roughly {off:F1} dB {(quiet ? "quieter" : "louder")} than that.",
            yesTip: quiet
                ? "Makes the whole video louder so viewers do not have to turn their volume up."
                : "Makes the whole video quieter so viewers do not have to turn their volume down.",
            noTip: "Leaves the sound exactly as you recorded it.");

        return win;
    }

    /// <summary>
    /// Builds the HARSH PEAK warning — the average is acceptable but there is a sudden bang.
    /// </summary>
    public static AudioFixPromptWindow ForHarshPeaks(LoudnessReading reading)
    {
        var win = new AudioFixPromptWindow();

        win.Title = "Warning — Sudden Loud Moment";
        win.SetText(
            title: "Warning — Sudden Loud Moment",
            message: "The uploaded video contains a sudden moment that is far louder than the rest of it. " +
                     "On headphones this can genuinely hurt, because it arrives without warning.",
            question: "Would you like to flatten that spike so it is no louder than the rest of the video?",
            detail: $"Loudest instant: {reading.TruePeakDbtp:F1} dBTP, which is about " +
                    $"{reading.PeakAboveAverageLu:F0} dB above this video's own average of {reading.IntegratedLufs:F1} LUFS.",
            yesTip: "Softens only the sudden loud moments. The rest of the video is left untouched.",
            noTip: "Leaves the loud moment exactly as you recorded it.");

        return win;
    }

    private void SetText(string title, string message, string question, string detail, string yesTip, string noTip)
    {
        var t = this.FindControl<TextBlock>("DialogTitle");
        if (t != null) t.Text = title;

        var m = this.FindControl<TextBlock>("MessageText");
        if (m != null) m.Text = message;

        var q = this.FindControl<TextBlock>("QuestionText");
        if (q != null) q.Text = question;

        var d = this.FindControl<TextBlock>("DetailText");
        if (d != null) d.Text = detail;

        var yes = this.FindControl<Button>("YesBtn");
        if (yes != null) ToolTip.SetTip(yes, yesTip);

        var no = this.FindControl<Button>("NoBtn");
        if (no != null) ToolTip.SetTip(no, noTip);
    }

    /// <summary>
    /// Resolves what to do about one detected audio problem, honouring anything the user has
    /// already told us. Shows the dialog only when the stored preference is
    /// <see cref="AudioFixPrompt.Ask"/>, and persists a new preference when the user sets one.
    /// </summary>
    /// <returns>True when the fix should be applied to this video.</returns>
    public static async Task<bool> ResolveAsync(
        Window owner,
        AudioFixPrompt current,
        Func<AudioFixPromptWindow> buildDialog,
        Action<AudioFixPrompt> persist,
        string logTag)
    {
        switch (current)
        {
            case AudioFixPrompt.AlwaysApply:
                RuntimeLog.Info(logTag, "Applying the fix automatically (user chose 'always' previously).");
                return true;

            case AudioFixPrompt.NeverApply:
                RuntimeLog.Info(logTag, "Skipping the fix (user chose 'never' previously).");
                return false;
        }

        var dialog = buildDialog();
        await dialog.ShowDialog(owner);

        var decision = dialog.Decision;
        if (decision.Remember != AudioFixPrompt.Ask)
        {
            persist(decision.Remember);
            RuntimeLog.Info(logTag, $"User answered {(decision.Apply ? "YES" : "NO")} and asked to remember it as {decision.Remember}.");
        }
        else
        {
            RuntimeLog.Info(logTag, $"User answered {(decision.Apply ? "YES" : "NO")} for this video only.");
        }

        return decision.Apply;
    }
}
