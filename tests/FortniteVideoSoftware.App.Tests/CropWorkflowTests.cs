using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using FortniteVideoSoftware.App;
using FortniteVideoSoftware.App.Controls;
using FortniteVideoSoftware.App.Infrastructure;
using FortniteVideoSoftware.App.ViewModels;
using FortniteVideoSoftware.Core.Infrastructure;
using FortniteVideoSoftware.Core.Ipc;
using SkiaSharp;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(FortniteVideoSoftware.App.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FortniteVideoSoftware.App.Tests;

public class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class CropWorkflowTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FvsCropUiTests_" + Guid.NewGuid().ToString("N"));
    private string? _previousRoot;
    private ApplicationPaths Paths => new(_root);

    public Task InitializeAsync()
    {
        _previousRoot = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _root);
        SettingsManager.Instance.ActiveMaskOverlay = "Fortnite";
        return Task.CompletedTask;
    }

    [AvaloniaFact]
    public void FreshInstallSeedsFortniteWithoutRecursion()
    {
        MaskOverlayManager.EnsureDefaults();
        Assert.True(JsonNode.DeepEquals(CropConfigDefaults.Create(), AtomicJsonFile.ReadObject(Paths.CropCoordinatesFile)));
        Assert.True(JsonNode.DeepEquals(CropConfigDefaults.Create(), AtomicJsonFile.ReadObject(Profile("Fortnite"))));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DamagedFortniteIsBackedUpAndRestored(bool malformedJson)
    {
        Directory.CreateDirectory(MaskOverlayManager.ProfilesDirectory);
        var damaged = CropConfigDefaults.Create();
        damaged["scales"]!["loot"] = "1/0";
        string original = malformedJson ? "{broken" : damaged.ToJsonString();
        File.WriteAllText(Profile("Fortnite"), original);
        MaskOverlayManager.EnsureDefaults();
        Assert.True(JsonNode.DeepEquals(CropConfigDefaults.Create(), AtomicJsonFile.ReadObject(Profile("Fortnite"))));
        string backup = Assert.Single(Directory.GetFiles(MaskOverlayManager.ProfilesDirectory, "Fortnite.json.invalid.*.bak"));
        Assert.Equal(original, File.ReadAllText(backup));
    }

    [AvaloniaFact]
    public void ExistingFortniteEditsSurviveAndDoNotSeedFromAnotherActiveProfile()
    {
        Paths.EnsureWritableDirectories();
        Directory.CreateDirectory(MaskOverlayManager.ProfilesDirectory);
        var custom = CropConfigDefaults.Create();
        custom["overlays"]!["loot"]!["x"] = 123;
        AtomicJsonFile.WriteObject(Paths.CropCoordinatesFile, custom);
        MaskOverlayManager.EnsureDefaults();
        Assert.True(JsonNode.DeepEquals(CropConfigDefaults.Create(), AtomicJsonFile.ReadObject(Profile("Fortnite"))));
        AtomicJsonFile.WriteObject(Profile("Fortnite"), custom);
        MaskOverlayManager.EnsureDefaults();
        Assert.True(JsonNode.DeepEquals(custom, AtomicJsonFile.ReadObject(Profile("Fortnite"))));
    }

    [AvaloniaFact]
    public void ProfileWriteFailureIsReported()
    {
        MaskOverlayManager.EnsureDefaults();
        SettingsManager.Instance.ActiveMaskOverlay = "Blocked";
        Directory.CreateDirectory(Profile("Blocked")); // A directory cannot be replaced by a JSON file.
        Assert.False(MaskOverlayManager.SyncActiveProfileFromCurrentConfig());
    }

    [AvaloniaFact]
    public void SpectatingStartsOnAndNoMaskRemainsEmpty()
    {
        var vm = new MainViewModel(Paths);
        Assert.True(vm.IsSpectating);
        vm.IsSpectating = false;
        vm.ApplyDefaults();
        Assert.True(vm.IsSpectating);
        vm.ApplyMaskProfile(CropConfigDefaults.NoMaskProfileName);
        Assert.False(vm.IsSpectating);
        Assert.False(vm.IsTeammates);
    }

    [AvaloniaTheory]
    [InlineData("save", ConfirmDialogWindow.SaveChangesChoice.Save)]
    [InlineData("discard", ConfirmDialogWindow.SaveChangesChoice.Discard)]
    [InlineData("back", ConfirmDialogWindow.SaveChangesChoice.BackToEditing)]
    [InlineData("close", ConfirmDialogWindow.SaveChangesChoice.BackToEditing)]
    [InlineData("enter", ConfirmDialogWindow.SaveChangesChoice.BackToEditing)]
    [InlineData("escape", ConfirmDialogWindow.SaveChangesChoice.BackToEditing)]
    public async Task UnsavedPromptHasClearActionsAndSafeKeyboardDefaults(string action, ConfirmDialogWindow.SaveChangesChoice expected)
    {
        var owner = new Window();
        owner.Show();
        var result = ConfirmDialogWindow.AskSaveChangesAsync(owner, "Fortnite", "closing Crop Tools");
        var dialog = Assert.IsType<ConfirmDialogWindow>(Assert.Single(owner.OwnedWindows));
        var save = dialog.FindControl<Button>("YesBtn")!;
        var back = dialog.FindControl<Button>("NoBtn")!;
        var discard = dialog.FindControl<Button>("AltBtn")!;
        Assert.Equal("Save changes", save.Content);
        Assert.Equal("Back to editing", back.Content);
        Assert.Equal("Discard changes", discard.Content);
        Assert.Contains("Success", save.Classes);
        Assert.Contains("Secondary", back.Classes);
        Assert.Contains("Danger", discard.Classes);
        Assert.False(save.IsDefault);
        Assert.True(back.IsDefault);
        Assert.True(back.IsCancel);
        switch (action)
        {
            case "save": save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
            case "discard": discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
            case "back": back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
            case "close": dialog.Close(); break;
            case "enter": dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); break;
            case "escape": dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); break;
        }
        Assert.Equal(expected, await result.WaitAsync(TimeSpan.FromSeconds(3)));
        owner.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddingHudRestoresTemporaryZoomAndPreservesManualZoom(bool temporaryZoom)
    {
        var window = new CropToolWindow(); // Never shown: no native video player is started.
        var source = Nested("SourceRect", 1500, 900, 160, 80);
        Set(window, "_sourceSelection", source);
        string snapshot = Path.Combine(_root, "frame.png");
        using (var bitmap = new SKBitmap(1920, 1080))
        using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(snapshot)) data.SaveTo(stream);
        Set(window, "_snapshotPath", snapshot);
        Invoke(window, "ApplySnapshotZoomInternal", 2.0, false, true);
        if (temporaryZoom) Set(window, "_preZoomState", Nested("PreZoomState", 0.5, true, false, new Vector()));
        var role = ((Array)typeof(CropToolWindow).GetField("Roles", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).GetValue(0)!;
        await (Task)Invoke(window, "AddCurrentSelection", role)!;
        Assert.True((bool)Get(window, "_dirty")!);
        Assert.Null(Get(window, "_sourceSelection"));
        Assert.Null(Get(window, "_preZoomState"));
        Assert.Equal(temporaryZoom ? 0.5 : 2.0, (double)Invoke(window, "CurrentZoom")!);
        Assert.Equal(temporaryZoom, Get(window, "_snapshotFitMode"));
        Assert.Equal(!temporaryZoom, Get(window, "_userZoomed"));
        Set(window, "_dirty", false);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private string Profile(string name) => Path.Combine(MaskOverlayManager.ProfilesDirectory, name + ".json");
    private static object? Get(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj);
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static object? Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);
    private static object Nested(string name, params object[] args) => Activator.CreateInstance(typeof(CropToolWindow).GetNestedType(name, BindingFlags.NonPublic)!, args)!;

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _previousRoot);
        for (int attempt = 0; ; attempt++)
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); return; }
            catch (IOException) when (attempt < 5) { await Task.Delay(100); }
        }
    }
}
