
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FreeVideoStudio.App;

public partial class AvaloniaApp : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

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
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        FreeVideoStudio.Core.Media.VideoRenderMode.Initialize();
        Infrastructure.SettingsManager.Load();
        Infrastructure.ThemeManager.ApplyFromSettings();

        Controls.Tactile.EnableGlobalRipple();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => UiSoundEffect.Shutdown();

            desktop.Exit += (_, _) => TaskbarProgress.Shutdown();

            var argsList = desktop.Args ?? System.Array.Empty<string>();
            bool isInstallWorker = System.Linq.Enumerable.Any(argsList, a => a.Equals("--install-worker", System.StringComparison.OrdinalIgnoreCase));
            bool isCleanupWorker = System.Linq.Enumerable.Any(argsList, a => a.Equals("--cleanup-worker", System.StringComparison.OrdinalIgnoreCase));
            bool isCropTool = System.Linq.Enumerable.Any(argsList, a => a.Equals("--crop-tool", System.StringComparison.OrdinalIgnoreCase));
            bool isMerger = System.Linq.Enumerable.Any(argsList, a => a.Equals("--merger", System.StringComparison.OrdinalIgnoreCase));

            if (isInstallWorker || isCleanupWorker)
            {
                string title = isInstallWorker ? "Free Video Studio Setup" : "Free Video Studio Uninstall";
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
                        global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
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
                if (argsList.Contains("--upgrade-health"))
                {
                    var window = desktop.MainWindow;
                    window.IsEnabled = false;
                    window.Loaded += async (_, _) =>
                    {
                        try
                        {
                            await Services.UpgradeCoordinator.ConfirmWindowAsync(argsList);
                            window.IsEnabled = true;
                        }
                        catch (System.Exception ex)
                        {
                            RuntimeLog.Fail("Upgrade first launch", ex.ToString());
                            desktop.Shutdown(1);
                        }
                    };
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
