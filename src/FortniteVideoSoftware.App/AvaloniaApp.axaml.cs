using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FortniteVideoSoftware.App;

public partial class AvaloniaApp : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // ==================================================================================
        // UI SOUND DISPATCH (audit round 5)
        // ----------------------------------------------------------------------------------
        // All routing decisions moved into UiSoundRouter — read the header comment there for
        // why the previous style-class sniffing was wrong (AUDIO_04/07/10/11). This method now
        // does one thing: forward clicks to the router.
        //
        // AUDIO_09: a second handler is registered for MenuItem. The old code listened to
        // Button.ClickEvent only, so all 17 menu entries across the Main App and the Merger
        // were silent — including entries that are the SAME action as a button that did make a
        // sound (MenuVideoMerger vs VideoMergerButton, MenuCropSettings vs CropSettingsButton).
        // ==================================================================================
        Avalonia.Controls.Button.ClickEvent.AddClassHandler<Avalonia.Controls.Button>(
            (sender, e) => DispatchUiSound(sender),
            Avalonia.Interactivity.RoutingStrategies.Bubble, true);

        Avalonia.Controls.MenuItem.ClickEvent.AddClassHandler<Avalonia.Controls.MenuItem>(
            (sender, e) => DispatchUiSound(sender),
            Avalonia.Interactivity.RoutingStrategies.Bubble, true);
    }

    /// <summary>
    /// Never let a UI sound break a user action: the router and the engine are both
    /// non-throwing by contract, and this is the final guard around both.
    /// </summary>
    private static void DispatchUiSound(object? sender)
    {
        try
        {
            var cue = UiSoundRouter.Resolve(sender);
            if (cue.HasValue) UiSoundEffect.Play(cue.Value);
        }
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine(ex.ToString()); }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        FortniteVideoSoftware.Core.Media.VideoRenderMode.Initialize();
        Infrastructure.SettingsManager.Load();
        Infrastructure.ThemeManager.ApplyFromSettings();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // AUDIO_13: the sound engine holds a WaveOutEvent and a mixer. The previous
            // implementation disposed nothing at all — six SoundPlayer instances and their
            // MemoryStreams simply leaked to process exit. Deterministic teardown, idempotent.
            desktop.Exit += (_, _) => UiSoundEffect.Shutdown();

            // IDEA_3: release the shell's ITaskbarList3 reference. Same reasoning as the sound
            // engine above — a COM object held to process exit is a dangling reference.
            desktop.Exit += (_, _) => TaskbarProgress.Shutdown();

            var argsList = desktop.Args ?? System.Array.Empty<string>();
            bool isInstallWorker = System.Linq.Enumerable.Any(argsList, a => a.Equals("--install-worker", System.StringComparison.OrdinalIgnoreCase));
            bool isCleanupWorker = System.Linq.Enumerable.Any(argsList, a => a.Equals("--cleanup-worker", System.StringComparison.OrdinalIgnoreCase));
            bool isCropTool = System.Linq.Enumerable.Any(argsList, a => a.Equals("--crop-tool", System.StringComparison.OrdinalIgnoreCase));
            bool isMerger = System.Linq.Enumerable.Any(argsList, a => a.Equals("--merger", System.StringComparison.OrdinalIgnoreCase));

            if (isInstallWorker || isCleanupWorker)
            {
                string title = isInstallWorker ? "Fortnite Video Software Setup" : "Fortnite Video Software Uninstall";
                var window = new DeploymentProgressWindow(title, DeploymentFootprint.InstallReportPath);
                
                void OnProgressHandler(string detail, int? percent)
                {
                    window.UpdateStatus(detail);
                    if (percent.HasValue) window.UpdateProgress(percent.Value);
                }
                
                DeploymentReporter.OnProgress += OnProgressHandler;
                window.Closed += (s, e) => DeploymentReporter.OnProgress -= OnProgressHandler;

                desktop.MainWindow = window;
                Task.Run(async () =>
                {
                    int exitCode = 1;
                    try
                    {
                        exitCode = isInstallWorker 
                            ? await DeploymentLifecycle.RunInstallAsync(argsList)
                            : await DeploymentLifecycle.RunUninstallWorkerAsync(argsList);
                    }
                    catch (System.Exception ex)
                    {
                        window.ShowFailureAndWait(ex.Message);
                        return;
                    }
                    
                    bool quiet = System.Linq.Enumerable.Any(argsList, a => a.Equals("--quiet", System.StringComparison.OrdinalIgnoreCase));
                    if (quiet)
                    {
                        System.Environment.Exit(exitCode);
                    }

                    if (exitCode == 0)
                    {
                        await window.ShowSuccessAndCloseAsync();
                        System.Environment.Exit(0);
                    }
                    else
                    {
                        window.ShowFailureAndWait("Installation encountered an error. Please review the log.");
                    }
                });
            }
            else if (isCropTool)
            {
                desktop.MainWindow = new CropToolWindow();
            }
            else if (isMerger)
            {
                desktop.MainWindow = new VideoMergerWindow();
            }
            else
            {
                desktop.MainWindow = new MainWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
