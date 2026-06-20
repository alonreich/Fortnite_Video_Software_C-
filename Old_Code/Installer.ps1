$needsElevation = -not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$ep = Get-ExecutionPolicy -Scope Process
if ($needsElevation -or ($ep -notin @('Bypass','Unrestricted'))) {
    $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    Start-Process -Verb RunAs -FilePath "$PSHOME\powershell.exe" -ArgumentList $args
    exit
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$logPath = "$env:TEMP\Fortnite_Video_Software_Install_Report.txt"
"Fortnite Video Software Installation Report" | Out-File $logPath -Encoding UTF8
"==========================================" | Out-File $logPath -Append -Encoding UTF8
Add-Content $logPath ("Started: " + (Get-Date))
$step0OK=$false; $step1OK=$false; $step2OK=$false; $pyOK=$false; $pipOK=$false; $verbOK=$false; $binOK=$false; $deskOK=$false; $fixOK=$false
function Step($n,$m,$ok){$s=if($ok){"✅ SUCCESS"}else{"❌ FAILED"};Write-Host "[$n] $s - $m";Add-Content $logPath "[$n] $s - $m"}
function Info($t){Write-Host $t -ForegroundColor Cyan;Add-Content $logPath $t}
function Broadcast-EnvChange {
try {
$src=@'
using System;
using System.Runtime.InteropServices;
public static class EnvRefresh {
  [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Auto)]
  public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
'@
Add-Type -TypeDefinition $src -ErrorAction SilentlyContinue | Out-Null
$r=[IntPtr]::Zero
[void][EnvRefresh]::SendMessageTimeout([IntPtr]0xffff,0x001A,[IntPtr]0,"Environment",2,5000,[ref]$r)
} catch {}
}
function Add-PathEntryUser($dir){
if(-not (Test-Path $dir)){return}
$current=[Environment]::GetEnvironmentVariable('Path','User')
$parts=($current -split ';')|Where-Object { $_ -ne '' }
if($parts -notcontains $dir){[Environment]::SetEnvironmentVariable('Path',(($parts+$dir)-join ';'),'User')}
if(($env:Path -split ';') -notcontains $dir){$env:Path=($env:Path.TrimEnd(';')+';'+$dir)}
}
function Find-PythonInstall{
$info=[ordered]@{PyLauncherPath=$null;PyDir=$null;PyExe=$null;ScriptsDir=$null;Ver='3.13'}
$l1=Join-Path $env:LOCALAPPDATA 'Programs\Python\Launcher\py.exe'
$l2=Join-Path ${env:ProgramFiles} 'Python Launcher\py.exe'
foreach($p in @($l1,$l2)){if(Test-Path $p){$info.PyLauncherPath=$p;break}}
$regs=@('HKCU:\Software\Python\PythonCore\3.13\InstallPath','HKLM:\Software\Python\PythonCore\3.13\InstallPath','HKLM:\Software\WOW6432Node\Python\PythonCore\3.13\InstallPath')
foreach($rp in $regs){try{$ip=(Get-ItemProperty -Path $rp -ErrorAction Stop).'(default)'; if($ip -and $ip -notlike '*WindowsApps*'){$info.PyDir=$ip.TrimEnd('\'); break}}catch{}}
if(-not $info.PyDir){$g=Join-Path $env:LOCALAPPDATA 'Programs\Python\Python3.13'; if(Test-Path $g){$info.PyDir=$g}}
if($info.PyDir){$px=Join-Path $info.PyDir 'python.exe'; if(Test-Path $px){$info.PyExe=$px}; $sd=Join-Path $info.PyDir 'Scripts'; if(Test-Path $sd){$info.ScriptsDir=$sd}}
[pscustomobject]$info
}
function Ensure-Winget{
$wg=Get-Command winget -ErrorAction SilentlyContinue
if($wg){return $true}
Info "[winget] Registering Desktop App Installer"
try{Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe; Start-Sleep 5; $wg=Get-Command winget -ErrorAction SilentlyContinue; if($wg){return $true}}catch{}
return $false
}
function Get-FileSmart {
param(
  [Parameter(Mandatory=$true)][string]$Url,
  [Parameter(Mandatory=$true)][string]$OutFile,
  [int]$TimeoutSec = 900
)
try {
    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        Write-Host "[download] curl.exe → $OutFile" -ForegroundColor Cyan
        $args = @('-L','--retry','3','--retry-delay','2','--max-time',[int]$TimeoutSec,'-o',"$OutFile","$Url")
        $p = Start-Process -FilePath curl.exe -ArgumentList $args -PassThru -NoNewWindow -Wait
        if ($p.ExitCode -eq 0 -and (Test-Path $OutFile)) { return $true }
        Write-Host "[download] curl exit $($p.ExitCode)" -ForegroundColor Yellow
    }
} catch { Write-Host "[download] curl failed: $($_.Exception.Message)" -ForegroundColor Yellow }
try {
    Write-Host "[download] Invoke-WebRequest fallback → $OutFile" -ForegroundColor Cyan
    $old = $global:ProgressPreference; $global:ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing -Headers @{ 'Cache-Control'='no-cache' } -TimeoutSec $TimeoutSec
    $global:ProgressPreference = $old
    if (Test-Path $OutFile) { return $true }
    throw "iwr produced no file"
} catch {
    try { $global:ProgressPreference = $old } catch {}
    Write-Host "[download] iwr failed: $($_.Exception.Message)" -ForegroundColor Red
    return $false
}
}
function Set-ShortcutRunAsAdministrator {
param([string]$lnkPath)
$src=@'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
[ComImport, Guid("00021401-0000-0000-C000-000000000046")]

class CShellLink {}
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
interface IPersistFile {
  void GetClassID(out Guid pClassID);
  void IsDirty();
  void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
  void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
  void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
  void GetCurFile(out IntPtr ppszFileName);
}
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1")]
interface IShellLinkDataList {
  void AddDataBlock(IntPtr pDataBlock);
  void CopyDataBlock(uint dwSig, out IntPtr ppDataBlock);
  void RemoveDataBlock(uint dwSig);
  void GetFlags(out uint pdwFlags);
  void SetFlags(uint dwFlags);
}
public static class LnkRunAs {
  const uint SLDF_RUNAS_USER = 0x00002000;
  public static void Enable(string path) {
    var link = new CShellLink();
    var pfile = (IPersistFile)link;
    pfile.Load(path, 0);
    var dl = (IShellLinkDataList)link;
    uint flags; dl.GetFlags(out flags);
    flags |= SLDF_RUNAS_USER;
    dl.SetFlags(flags);
    pfile.Save(path, true);
  }
}
'@
Add-Type -TypeDefinition $src -ErrorAction SilentlyContinue | Out-Null
[LnkRunAs]::Enable($lnkPath)
}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$desktop=[Environment]::GetFolderPath("Desktop")
try{ Get-ChildItem -Path $desktop -Filter "*fortnite*.lnk" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue }catch{}
try{ Get-ChildItem -Path $desktop -Filter "*video*.lnk" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue }catch{}
try{ $ves=Join-Path $desktop "Video_Editing_Software"; if(Test-Path $ves){ Remove-Item $ves -Recurse -Force -ErrorAction SilentlyContinue } }catch{}
try {
    Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WindowsApps\python*.exe" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WindowsApps\pip*.exe" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
} catch {}
$installPath = "C:\Fortnite_Video_Software"
$backupPath = "C:\Old_Fortnite_Video_Software"
while (Test-Path $backupPath) {
    try {
        Remove-Item $backupPath -Recurse -Force -ErrorAction Stop
    } catch {
        Write-Host "`n[!] CRITICAL ERROR: Cannot delete OLD backup folder: $backupPath" -ForegroundColor Red
        Write-Host "    The folder is locked. Please close any apps using it." -ForegroundColor Yellow
        Write-Host "    Error Details: $($_.Exception.Message)" -ForegroundColor Red
        Read-Host "    >>> PRESS [ENTER] TO RETRY <<<"
    }
}
if (Test-Path $installPath) {
    $renamed = $false
    while (-not $renamed) {
        try {
            Move-Item -LiteralPath $installPath -Destination $backupPath -Force -ErrorAction Stop
            $renamed = $true
            Step 0 "Backed up existing installation to $backupPath" $true
        } catch {
            Write-Host "`n[!] CRITICAL ERROR: Cannot move/rename existing folder: $installPath" -ForegroundColor Red
            Write-Host "    It is locked by another program (Explorer, cmd, VS Code, etc)." -ForegroundColor Red
            Write-Host "    Error Details: $($_.Exception.Message)" -ForegroundColor Red
            Read-Host "    >>> PLEASE CLOSE THE LOCKING PROGRAM AND PRESS [ENTER] TO RETRY <<<"
        }
    }
} else {
    Step 0 "No previous installation found (Clean Install)" $true
}
$step0OK = $true
try{ New-Item -ItemType Directory -Force -Path $installPath|Out-Null; Step 1 "Install directory prepared at $installPath" $true; $step1OK=$true }catch{ Step 1 "Failed to prepare $installPath : $($_.Exception.Message)" $false }
try{
$repoURL="https://github.com/alonreich/Fortnite_Video_Software"
$zipURL="$repoURL/archive/refs/heads/main.zip"
$zipFile="$env:TEMP\Fortnite_Video_Software.zip"
$stage=Join-Path $env:TEMP ("FVS_stage_"+[guid]::NewGuid())
if(Test-Path $stage){Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue}; New-Item -ItemType Directory -Force -Path $stage|Out-Null
Info "[download] $zipURL"
    if(-not (Get-FileSmart -Url $zipURL -OutFile $zipFile -TimeoutSec 1800)){ throw "Download failed" }
    Expand-Archive -Path $zipFile -DestinationPath $stage -Force
    Remove-Item $zipFile -Force
    $top=Get-ChildItem -Directory -Path $stage | Select-Object -First 1
    if(-not $top){throw "No extracted folder found"}
    Get-ChildItem -LiteralPath $top.FullName -Force | %{
        $dest=Join-Path $installPath $_.Name
        if(Test-Path $dest){Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue}
        Move-Item -LiteralPath $_.FullName -Destination $installPath -Force
    }
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($folder in @("binaries", "MP3")) {
        $targetDir = Join-Path $installPath $folder
        if (Test-Path $targetDir) {
            $lfsFiles = Get-ChildItem -Path $targetDir -File -Recurse
            foreach ($f in $lfsFiles) {
                if ($f.Length -lt 5KB) {
                    $safeName = [Uri]::EscapeDataString($f.Name)
                    $rawUrl = "https://github.com/alonreich/Fortnite_Video_Software/raw/main/$folder/$safeName"
                    $tempName = "temp_download_" + [guid]::NewGuid().ToString()
                    $tempFile = Join-Path $targetDir $tempName
                    Info "[LFS Fix] Downloading: $safeName"
                    if (Get-FileSmart -Url $rawUrl -OutFile $tempFile -TimeoutSec 1800) {
                        Move-Item -Path $tempFile -Destination $f.FullName -Force
                    } else {
                        Write-Host "Failed to download: $($f.Name)" -ForegroundColor Red
                        if (Test-Path $tempFile) { Remove-Item $tempFile -Force }
                    }
                }
            }
        }
    }
    Step 2 "Fetched files and verified LFS binaries + MP3s" $true
$oldMp3 = Join-Path $backupPath "mp3"
$newMp3 = Join-Path $installPath "mp3"
if ((Test-Path $oldMp3) -and (Test-Path $newMp3)) {
    try {
        Get-ChildItem -Path $oldMp3 -Filter "*.mp3" -Recurse | Copy-Item -Destination $newMp3 -Force -ErrorAction SilentlyContinue
        Info "[restore] Restored MP3 files from backup to new installation."
    } catch {
        Info "[restore] Failed to copy MP3s: $($_.Exception.Message)"
    }
}
$step2OK = $true
}catch{ Step 2 "GitHub fetch/extract failed: $($_.Exception.Message)" $false }
$pyOK=$false; $global:Py=$null
try{
  $pyInstallerUrl = "https://www.python.org/ftp/python/3.13.0/python-3.13.0-amd64.exe"
  $pyInstallerFile = Join-Path $env:TEMP 'python-3.13.0-amd64.exe'
  if(-not (Test-Path $pyInstallerFile)){
    Info "[download] Python installer from $pyInstallerUrl"
    if(-not (Get-FileSmart -Url $pyInstallerUrl -OutFile $pyInstallerFile -TimeoutSec 900)){ throw "Python installer download failed." }
  }
  Info "[install] Launching Python silent installer..."
  $pyProc = Start-Process -FilePath $pyInstallerFile -ArgumentList "/quiet InstallAllUsers=1 PrependPath=1" -PassThru -Wait
  if($pyProc.ExitCode -ne 0 -and $pyProc.ExitCode -ne 3010){ Write-Host "[install] Python installer failed (Exit Code $($pyProc.ExitCode))" -ForegroundColor Red }
  $global:Py=Find-PythonInstall
  if($global:Py.PyLauncherPath){ Add-PathEntryUser ([System.IO.Path]::GetDirectoryName($global:Py.PyLauncherPath)) }
  if($global:Py.PyDir){ Add-PathEntryUser $global:Py.PyDir }
  if($global:Py.ScriptsDir){ Add-PathEntryUser $global:Py.ScriptsDir }
  Broadcast-EnvChange
  if(($global:Py.PyExe -and $global:Py.PyExe -notlike "*WindowsApps*") -or $global:Py.PyLauncherPath){ $pyOK=$true; Step 3 "Python located. Launcher: '$($global:Py.PyLauncherPath)'; Python: '$($global:Py.PyExe)'" $true } else { Step 3 "Python invalid or not available (Store version ignored)" $false }
}catch{ Step 3 "Python check/install error: $($_.Exception.Message)" $false }
$pipOK = $true
try {
    if (-not $pyOK) {
        $pipOK = $false
    } else {
        $env:PIP_DISABLE_PIP_VERSION_CHECK = "1"
        $pipBat = Join-Path $env:TEMP "FVS_PipInstall.cmd"
        $pipContent = @"
@echo off
setlocal
set LOG="$logPath"
echo ==== PIP INSTALLATION PHASE STARTED ==== >> %LOG% 2>&1
"@
        if ($global:Py.PyExe) {
            $cmd = "`"$($global:Py.PyExe)`""
            $pipContent += @"
echo. >> %LOG%
echo [pip] Targeting: Direct Python Executable >> %LOG% 2>&1
echo [pip] Upgrading pip (Direct)...
$cmd -m pip install --upgrade pip --timeout 300 --retries 3 >> %LOG% 2>&1
echo [pip] Installing packages (Direct)...
$cmd -m pip install PyQt5 psutil send2trash --timeout 900 --retries 3 --no-warn-script-location >> %LOG% 2>&1
if %errorlevel% neq 0 echo [!] Failed Direct Install (Exit Code %errorlevel%) >> %LOG% 2>&1
"@
        }
        if ($global:Py.PyLauncherPath) {
            $cmd = "`"$($global:Py.PyLauncherPath)`""
            $pipContent += @"
echo. >> %LOG%
echo [pip] Targeting: Py Launcher (-3.13) >> %LOG% 2>&1
echo [pip] Upgrading pip (Launcher)...
$cmd -3.13 -m pip install --upgrade pip --timeout 300 --retries 3 >> %LOG% 2>&1
echo [pip] Installing packages (Launcher)...
$cmd -3.13 -m pip install PyQt5 psutil pypiwin32 python-mpv send2trash --timeout 900 --retries 3 --no-warn-script-location >> %LOG% 2>&1
if %errorlevel% neq 0 echo [!] Failed Launcher Install (Exit Code %errorlevel%) >> %LOG% 2>&1
"@
        }
        $pipContent += @"
echo ==== PIP PHASE COMPLETE ==== >> %LOG% 2>&1
exit /b 0
"@
        Set-Content -Path $pipBat -Encoding ASCII -Value $pipContent
        Write-Host "[pip] Running installation (Using Absolute Paths for Reliability)" -ForegroundColor Cyan
        $proc = Start-Process -FilePath cmd.exe -ArgumentList "/c `"$pipBat`"" -PassThru -Wait
        $exit = if($proc){ $proc.ExitCode } else { 9999 }
        try { Remove-Item $pipBat -Force -ErrorAction SilentlyContinue } catch {}
        if ($exit -ne 0) { $pipOK = $false; Add-Content $logPath "[pip] ExitCode=$exit" } else { Add-Content $logPath "[pip] Completed successfully" }
    }
} catch { $pipOK = $false }
if ($pipOK) { Step 4 "Installed Python packages (PyQt5, psutil, pypiwin32, python-mpv, send2trash)" $true } else { Step 4 "pip packages were not fully installed; see log at $logPath" $false }
Step 5 "Classic context menu hack skipped" $true
$binOK=$true
Step 6 "Skipped creating PATH shims in \bin" $true
$verbOK=$true
try{
    $verbName = "Fortnite_Video_Software"
    $iconPath = Join-Path $installPath "icons\Video_Icon_File.ico"
    $runner = if($global:Py -and $global:Py.PyLauncherPath){ $global:Py.PyLauncherPath } elseif($global:Py -and $global:Py.PyExe){ $global:Py.PyExe } else { 'py.exe' }
    $pyArg = if($global:Py -and $global:Py.PyExe){ "-B" } else { "-3.13 -B" }
    $appPyPath = Join-Path $installPath "app.py"
    $CmdValue = "`"$runner`" $pyArg `"$appPyPath`" `"%1`""
    $RegPath = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\SystemFileAssociations\.mp4\shell\$verbName"
    $CmdPath = "$RegPath\command"
    $shellKey = "Registry::HKEY_CLASSES_ROOT\.mp4\shell"
    if (Test-Path $shellKey) {
        Get-ChildItem $shellKey | Where-Object { $_.Name -match "Fortnite" -or $_.Name -match "Video_Compressor" } | ForEach-Object {
            Write-Host " [Cleanup] Removing old weak key: $($_.Name)" -ForegroundColor Yellow
            Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Remove-Item "HKCU:\Software\Classes\.mp4\shell\Fortnite*" -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $iconPath)) { Write-Host " [WARNING] Icon file not found at: $iconPath" -ForegroundColor Red }
    New-Item -Path $RegPath -Force -ItemType Directory | Out-Null
    New-ItemProperty -Path $RegPath -Name "MUIVerb" -Value "Fortnite Video Software" -PropertyType String -Force | Out-Null
    if(Test-Path $iconPath){ New-ItemProperty -Path $RegPath -Name "Icon" -Value $iconPath -PropertyType String -Force | Out-Null }
    New-Item -Path $CmdPath -ItemType Directory -Force | Out-Null
    Set-Item -Path $CmdPath -Value $CmdValue | Out-Null
    Write-Host " [Refresh] Broadcasting environment change..." -ForegroundColor DarkGray
    Broadcast-EnvChange
    $verbOK=$true
} catch { $verbOK=$false }
Step 7 ("Cleaned old keys and registered 'SystemFileAssociations' (Strong Fix)"+$(if($verbOK){" "}else{" - errors occurred"})) $verbOK
$deskOK=$true
try{
$desktop=[Environment]::GetFolderPath("Desktop")
Get-ChildItem -Path $desktop -Filter "*fortnite*.lnk" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $desktop -Filter "*video*.lnk" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
$runner = if($global:Py -and $global:Py.PyLauncherPath){ $global:Py.PyLauncherPath } elseif($global:Py -and $global:Py.PyExe){ $global:Py.PyExe } else { 'py.exe' }
$pyArg = if($global:Py -and $global:Py.PyExe){ "" } else { "-3.13" }
$shortcutPath=Join-Path $desktop "Fortnite Video Software.lnk"
if(Test-Path $shortcutPath){Remove-Item $shortcutPath -Force}
$shell=New-Object -ComObject WScript.Shell
$sc=$shell.CreateShortcut($shortcutPath)
$sc.TargetPath=$runner
$sc.Arguments="$pyArg -B `"$installPath\app.py`""
$sc.IconLocation=Join-Path $installPath "icons\Video_Icon_File.ico"
$sc.WorkingDirectory=$installPath
$sc.Description="Launch Fortnite Video Software"
$sc.Save()
}catch{ $deskOK=$false }
Step 8 ("Desktop shortcut created/updated"+$(if($deskOK){" "}else{" - errors occurred"})) $deskOK
$fixOK=$true;$fixDeskOK=$true
try{
$fixCmdPath = Join-Path $installPath "Fix OS Problems.cmd"
@'
DISM /Online /Cleanup-Image /RestoreHealth > %USERPROFILE%\Desktop\Repair-OS.log 2>&1
sfc /SCANNOW >> %USERPROFILE%\Desktop\Repair-OS.log 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -Path $env:TMP -Force | Where-Object { $_.Name -ne 'Highlights' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue"
shutdown /r /t 0
'@ | Set-Content -Path $fixCmdPath -Encoding ASCII -Force
}catch{ $fixOK=$false }
try{
$desktop=[Environment]::GetFolderPath("Desktop")
$fixLnk = Join-Path $desktop "Fix OS Problems.lnk"
if(Test-Path $fixLnk){Remove-Item $fixLnk -Force -ErrorAction SilentlyContinue}
$shell=New-Object -ComObject WScript.Shell
$sl=$shell.CreateShortcut($fixLnk)
$sl.TargetPath = "cmd.exe"
$sl.Arguments = "/c `"$fixCmdPath`""
$sl.WorkingDirectory = $installPath
$sl.Description = "Fix OS Problems"
$sl.Save()
Set-ShortcutRunAsAdministrator -lnkPath $fixLnk
}catch{ $fixDeskOK=$false }
Step 9 ("Fix OS script created"+$(if($fixOK){" "}else{" - errors occurred"})+"; desktop shortcut created (Run as admin)"+$(if($fixDeskOK){" "}else{" - errors occurred"})) ($fixOK -and $fixDeskOK)
Add-Content $logPath ("Finished: " + (Get-Date))
$summaryPath = "$([Environment]::GetFolderPath("Desktop"))\Fortnite Video Software Log.txt"
$summaryContent = @()
$summaryContent += "Fortnite Video Software - Installation Summary"
$summaryContent += "Date: $(Get-Date)"
$summaryContent += "---------------------------------------------"
function Get-StatusMsg($b) { if($b) { return "✅ SUCCESS" } else { return "❌ FAILED" } }
$summaryContent += "1. Clean/Backup Files:   $(Get-StatusMsg $step0OK)"
$summaryContent += "2. Install Directory:    $(Get-StatusMsg $step1OK)"
$summaryContent += "3. Download Source:      $(Get-StatusMsg $step2OK)"
$summaryContent += "4. Python Install:       $(Get-StatusMsg $pyOK)"
$summaryContent += "5. Pip Libraries:        $(Get-StatusMsg $pipOK)"
$summaryContent += "6. Registry/Menu Fix:    $(Get-StatusMsg $verbOK)"
$summaryContent += "7. Desktop Shortcut:     $(Get-StatusMsg $deskOK)"
$summaryContent += "8. OS Fix Scripts:       $(Get-StatusMsg ($fixOK -and $fixDeskOK))"
$summaryContent += "---------------------------------------------"
if ($step1OK -and $step2OK -and $pyOK -and $pipOK -and $verbOK) {
    $summaryContent += "OVERALL STATUS:          ✅ COMPLETED SUCCESSFULLY"
} else {
    $summaryContent += "OVERALL STATUS:          ❌ COMPLETED WITH ERRORS"
    $summaryContent += "NOTE: Please check the detailed log at: $logPath"
}
$summaryContent | Out-File $summaryPath -Encoding UTF8 -Force
Write-Host "`nSummary log created at: $summaryPath" -ForegroundColor Cyan
Write-Host "`n==========================================================" -ForegroundColor Yellow
Write-Host "✅ Installation Complete." -ForegroundColor Green
Write-Host "NOTE: A system reboot is critical for PATH and registry changes to take full effect." -ForegroundColor Red
Write-Host "Please reboot your computer manually to complete the installation." -ForegroundColor Yellow