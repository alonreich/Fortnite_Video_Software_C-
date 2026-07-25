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
    private string _dismissalPath = string.Empty;
    private DispatcherTimer? _showTimer;

    /// <summary>Fired when the user clicks the card; passes the action Id.</summary>
    public event Action<string>? ActionInvoked;

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FortniteVideoSoftware", "Settings");

    public ButlerRibbon()
    {
        InitializeComponent();
        _container = this.FindControl<Border>("ContainerBorder");
        _cardButton = this.FindControl<Button>("CardButton");
        _dismissButton = this.FindControl<Button>("DismissButton");
        _iconText = this.FindControl<TextBlock>("IconText");
        _labelText = this.FindControl<TextBlock>("LabelText");

        _dismissalPath = Path.Combine(SettingsDir, "butler_dismissals.json");
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

        // Cancel any pending show timer
        _showTimer?.Stop();

        // Debounce: wait 800ms before showing to avoid flicker during rapid state changes
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
    }

    private void HideCard()
    {
        _currentAction = null;
        if (_container != null)
        {
            _container.Classes.Remove("ButlerVisible");
            _container.Classes.Add("ButlerHidden");
        }
    }

    private void LoadDismissals()
    {
        try
        {
            if (File.Exists(_dismissalPath))
            {
                var json = File.ReadAllText(_dismissalPath);
                var items = JsonSerializer.Deserialize<string[]>(json);
                if (items != null)
                {
                    foreach (var id in items)
                        _dismissed.Add(id);
                }
            }
        }
        catch { /* swallow — non-critical */ }
    }

    private void SaveDismissals()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(_dismissed);
            File.WriteAllText(_dismissalPath, json);
        }
        catch { /* swallow — non-critical */ }
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