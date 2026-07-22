using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FortniteVideoSoftware.App;

public partial class AvaloniaApp : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        Avalonia.Controls.Button.ClickEvent.AddClassHandler<Avalonia.Controls.Button>((sender, e) =>
        {
            if (sender is Avalonia.Controls.Button btn)
            {
                string name = btn.Name ?? "";
                
                if (btn.Classes.Contains("Success") || name == "ProcessButton" || name == "SaveButton")
                {
                    UiSoundEffect.PlayProcess();
                }
                else if (name == "MarkStartButton" || name == "MarkEndButton")
                {
                    UiSoundEffect.PlayMark();
                }
                else if (name == "VideoMergerButton" || name == "CropSettingsButton" || name == "GranularButton" || name == "AddMusicButton" || name == "VoiceOverButton" || name == "DetachOverlayButton")
                {
                    UiSoundEffect.PlayOpen();
                }
                else if (btn.Classes.Contains("Danger") || btn.Classes.Contains("DangerOutline") || name == "CancelButton" || name == "UndoButton" || name.Contains("Close", StringComparison.OrdinalIgnoreCase) || name.Contains("Exit", StringComparison.OrdinalIgnoreCase))
                {
                    UiSoundEffect.PlayClose();
                }
                else
                {
                    UiSoundEffect.PlayClick();
                }
            }
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        FortniteVideoSoftware.Core.Media.VideoRenderMode.Initialize();
        Infrastructure.SettingsManager.Load();
        Infrastructure.ThemeManager.ApplyFromSettings();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var argsList = desktop.Args ?? System.Array.Empty<string>();
            bool isInstallWorker = System.Linq.Enumerable.Any(argsList, a => a.Equals("--install-worker", System.StringComparison.OrdinalIgnoreCase));
            bool isCleanupWorker = System.Linq.Enumerable.Any(argsList, a => a.Equals("--cleanup-worker", System.StringComparison.OrdinalIgnoreCase));
            bool isCropTool = System.Linq.Enumerable.Any(argsList, a => a.Equals("--crop-tool", System.StringComparison.OrdinalIgnoreCase));
            bool isMerger = System.Linq.Enumerable.Any(argsList, a => a.Equals("--merger", System.StringComparison.OrdinalIgnoreCase));

            if (isInstallWorker || isCleanupWorker)
            {
                string title = isInstallWorker ? "Fortnite Video Software Setup" : "Fortnite Video Software Uninstall";
                var window = new DeploymentProgressWindow(title, DeploymentFootprint.InstallReportPath);
                
                DeploymentReporter.OnProgress += (detail, percent) =>
                {
                    window.UpdateStatus(detail);
                    if (percent.HasValue) window.UpdateProgress(percent.Value);
                };

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
