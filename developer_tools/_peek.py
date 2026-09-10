from pathlib import Path
import sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

def grep(path, words):
    lines = Path(path).read_text(encoding='utf-8-sig').split('\n')
    print(f'== {path} ==')
    for i, ln in enumerate(lines):
        if any(w in ln for w in words):
            print(f'{i+1}: {ln.rstrip()}')
    print()

grep('src/FortniteVideoSoftware.App/DeploymentLifecycle.cs',
     ['RelaunchInstallFromTempAsync', 'BuildInstallWorkerArgs'])
grep('src/FortniteVideoSoftware.App/Infrastructure/SettingsManager.cs',
     ['CurrentSchemaVersion', 'JsonSerializable', 'SettingsJsonContext', 'UiSoundsEnabled'])
grep('src/FortniteVideoSoftware.App/AvaloniaApp.axaml',
     ['Button.Success', 'Button.Secondary', 'Button.Danger', 'Button.Primary'])
grep('src/FortniteVideoSoftware.App/MainWindow.axaml.cs',
     ['WireComponents();'])



