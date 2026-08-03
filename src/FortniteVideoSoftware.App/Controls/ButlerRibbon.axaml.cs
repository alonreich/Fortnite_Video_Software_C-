using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FortniteVideoSoftware.App.Controls;

/// <summary>
/// Predictive "Butler" that suggests the next logical action based on editing state.
/// Heuristic engine observes workflow events and surfaces a single highest-value missing step.
/// Max 1 card visible at once; dismissals persist per-suggestion-type so it never nags.
/// </summary>
public partial class ButlerRibbon : UserControl
{
    private Border? _container;
    private Button? _cardButton;
    private Button? _dismissButton;
    private TextBlock? _iconText;
    private TextBlock? _labelText;

    private ButlerAction? _currentAction;
    private readonly HashSet<string> _dismissed = new();
    private DispatcherTimer? _showTimer;

    /// <summary>Fired when the user clicks the card; passes the action Id.</summary>
    public event Action<string>? ActionInvoked;

    public ButlerRibbon()
    {
        InitializeComponent();
        _container = this.FindControl<Border>("ContainerBorder");
        _cardButton = this.FindControl<Button>("CardButton");
        _dismissButton = this.FindControl<Button>("DismissButton");
        _iconText = this.FindControl<TextBlock>("IconText");
        _labelText = this.FindControl<TextBlock>("LabelText");

        LoadDismissals();

        if (_cardButton != null)
            _cardButton.Click += (_, _) =>
            {
                if (_currentAction != null)
                {
                    ActionInvoked?.Invoke(_currentAction.Id);
                    HideCard();
                }
            };

        if (_dismissButton != null)
            _dismissButton.Click += (_, _) =>
            {
                if (_currentAction != null)
                {
                    _dismissed.Add(_currentAction.Id);
                    SaveDismissals();
                }
                HideCard();
            };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Shows a suggestion card if the action hasn't been dismissed by the user.
    /// Only one card is visible at a time.
    /// </summary>
    public void Suggest(ButlerAction action)
    {
        if (_dismissed.Contains(action.Id))
            return;

        _showTimer?.Stop();

        _currentAction = action;
        _showTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _showTimer.Tick += (_, _) =>
        {
            _showTimer?.Stop();
            ShowCard(action);
        };
        _showTimer.Start();
    }

    /// <summary>Immediately hides and clears any current suggestion.</summary>
    public void Clear()
    {
        _showTimer?.Stop();
        HideCard();
    }

    private void ShowCard(ButlerAction action)
    {
        if (_iconText != null) _iconText.Text = action.Icon;
        if (_labelText != null) _labelText.Text = action.Label;
        if (_container != null)
        {
            _container.Classes.Remove("ButlerHidden");
            _container.Classes.Add("ButlerVisible");
        }
        IsVisible = true;
        Opacity = 1;
        IsHitTestVisible = true;
    }

    private void HideCard()
    {
        _currentAction = null;
        if (_container != null)
        {
            _container.Classes.Remove("ButlerVisible");
            _container.Classes.Add("ButlerHidden");
        }
        IsHitTestVisible = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!IsHitTestVisible)
            {
                Opacity = 0;
                IsVisible = false;
            }
        };
        timer.Start();
    }

    private const string DismissalFile = "butler_dismissals.json";

    private void LoadDismissals()
    {
        try
        {
            string json = FortniteVideoSoftware.Core.Infrastructure.UiStateStore.ReadText(DismissalFile);
            if (string.IsNullOrWhiteSpace(json)) return;

            // ISSUE_1: this was JsonSerializer.Deserialize<string[]>(json), which resolves the type
            // by reflection. The shipped build is NativeAOT with TrimMode=full, which strips that
            // machinery — so in the real EXE the call throws and the empty catch below ate it,
            // meaning dismissed suggestions silently came back on every launch while dev builds
            // looked fine. JsonNode.Parse needs no reflection and is what the rest of the codebase
            // already uses (see AtomicJsonFile.ReadObject).
            var array = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsArray();
            if (array != null)
            {
                foreach (var node in array)
                {
                    string? id = node?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(id)) _dismissed.Add(id);
                }
            }
        }
        catch (Exception ex)
        {
            // ISSUE_2: was `catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }`. Still non-fatal, but a swallowed failure now leaves a trail.
            RuntimeLog.Debug("Butler", $"Dismissal list could not be read: {ex.Message}");
        }
    }

    private void SaveDismissals()
    {
        try
        {
            // ISSUE_1: was JsonSerializer.Serialize(_dismissed) — same reflection problem as the
            // read above. Building the array by hand needs no reflection and survives trimming.
            var array = new System.Text.Json.Nodes.JsonArray();
            foreach (string id in _dismissed) array.Add(id);

            FortniteVideoSoftware.Core.Infrastructure.UiStateStore.WriteText(
                DismissalFile, array.ToJsonString());
        }
        catch (Exception ex)
        {
            // ISSUE_2: was `catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }`.
            RuntimeLog.Debug("Butler", $"Dismissal list could not be saved: {ex.Message}");
        }
    }
}

/// <summary>
/// A single predictive action suggestion for the Butler ribbon.
/// </summary>
public sealed class ButlerAction
{
    public required string Id { get; init; }
    public required string Icon { get; init; }
    public required string Label { get; init; }
    
}