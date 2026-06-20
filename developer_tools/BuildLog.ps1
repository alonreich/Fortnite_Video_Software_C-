# Mirrors Build.cmd console output to .\build.log (live console + ANSI-colored log file).
param(
    [string]$BatchPath = '.\Build.cmd',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$BatchArgs
)

$ErrorActionPreference = 'Continue'
$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')
$root = '.'
$logPath = '.\build.log'
$legacyResultPath = '.\build.result.txt'
$esc = [char]0x1B
$script:ErrorCount = 0
$script:WarningCount = 0

if ([System.IO.Path]::IsPathRooted($BatchPath)) {
    throw "BatchPath must be relative to the repository root."
}

Push-Location $repoRoot

function Format-AnsiLine {
    param([string]$Line, [string]$Level)
    switch ($Level) {
        'Error'   { return "$esc[91m$Line$esc[0m" }
        'Warning' { return "$esc[93m$Line$esc[0m" }
        'Success' { return "$esc[92m$Line$esc[0m" }
        'Banner'  { return "$esc[96m$Line$esc[0m" }
        'Verdict' { return "$esc[1m$Line$esc[0m" }
        default   { return $Line }
    }
}

function Test-BuildErrorLine {
    param([string]$Line)
    if ([string]::IsNullOrWhiteSpace($Line)) { return $false }
    if ($Line -match '(?i)\b0 Error\(s\)|\b0 errors\b') { return $false }
    if ($Line -match '(?i)^ERROR:|\bBuild FAILED\b|: error [A-Z]{1,5}\d{4,}:|error MSB\d+:|\.error\.|exit /b 1') { return $true }
    return $false
}

function Test-BuildWarningLine {
    param([string]$Line)
    if ([string]::IsNullOrWhiteSpace($Line)) { return $false }
    if ($Line -match '(?i)\b0 Warning\(s\)|\b0 warnings\b') { return $false }
    if ($Line -match 'ANSI colors in log') { return $false }
    if ($Line -match '(?i): warning |warning CS\d+|\bwarning\b') { return $true }
    return $false
}

function Get-LineLevel {
    param([string]$Line)
    if (Test-BuildErrorLine $Line) { return 'Error' }
    if (Test-BuildWarningLine $Line) { return 'Warning' }
    if ($Line -match '(?i)SUCCESS:|Build succeeded\.|Verified .*compile') { return 'Success' }
    if ($Line -match '^#{5,}') { return 'Banner' }
    return 'Info'
}

function Get-HostColor {
    param([string]$Level)
    switch ($Level) {
        'Error'   { return 'Red' }
        'Warning' { return 'Yellow' }
        'Success' { return 'Green' }
        'Banner'  { return 'Cyan' }
        'Verdict' { return 'White' }
        default   { return 'Gray' }
    }
}

function Write-LoggedLine {
    param([string]$Line, [string]$ForceLevel)
    if ($null -eq $Line) { return }
    $level = if ($ForceLevel) { $ForceLevel } else { Get-LineLevel -Line $Line }
    if (-not $ForceLevel) {
        if ($level -eq 'Error') { $script:ErrorCount++ }
        elseif ($level -eq 'Warning') { $script:WarningCount++ }
    }
    try { Write-Host $Line -ForegroundColor (Get-HostColor -Level $level) }
    catch { Write-Host $Line }
    Add-Content -LiteralPath $logPath -Value (Format-AnsiLine -Line $Line -Level $level) -Encoding utf8
}

function Get-BuildVerdict {
    param([int]$ExitCode)
    if ($ExitCode -ne 0 -or $script:ErrorCount -gt 0) {
        return @{
            Label = 'FAILED'
            Level = 'Error'
            Plain = 'FAIL'
        }
    }
    if ($script:WarningCount -gt 0) {
        return @{
            Label = 'SUCCESS WITH WARNINGS'
            Level = 'Warning'
            Plain = 'WARN'
        }
    }
    return @{
        Label = 'CLEAN SUCCESS'
        Level = 'Success'
        Plain = 'OK'
    }
}

function New-BuildSummaryOneLiner {
    param([hashtable]$Verdict, [int]$ExitCode, [TimeSpan]$Duration)
    $durationText = '{0:hh\:mm\:ss}' -f $Duration
    return '{0} | {1} errors | {2} warnings | exit {3} | {4}' -f $Verdict.Plain, $script:ErrorCount, $script:WarningCount, $ExitCode, $durationText
}

function New-VerdictBannerLines {
    param(
        [string]$VerdictLabel,
        [int]$ExitCode,
        [TimeSpan]$Duration,
        [string]$Placement
    )
    $bar = '*' * 78
    $durationText = '{0:hh\:mm\:ss}' -f $Duration
    $headline = "BUILD VERDICT: $VerdictLabel"
    $stats = "Errors: $($script:ErrorCount)  |  Warnings: $($script:WarningCount)  |  Exit code: $ExitCode  |  Duration: $durationText"
    $hint = if ($Placement -eq 'top') {
        'At-a-glance summary (one-liner above; full banner repeated at end of file). Transcript below.'
    } else {
        'At-a-glance summary (duplicate of top of file).'
    }
    return @(
        '',
        $bar,
        "* $headline".PadRight(77) + '*',
        "* $stats".PadRight(77) + '*',
        "* $hint".PadRight(77) + '*',
        $bar,
        ''
    )
}

function Write-VerdictToConsole {
    param([string]$OneLiner, [string[]]$Lines, [string]$Level)
    try { Write-Host $OneLiner -ForegroundColor (Get-HostColor -Level 'Verdict') }
    catch { Write-Host $OneLiner }
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            Write-Host ''
            continue
        }
        try { Write-Host $line -ForegroundColor (Get-HostColor -Level $Level) }
        catch { Write-Host $line }
    }
}

function Prepend-BuildLogSummary {
    param(
        [string]$OneLiner,
        [string[]]$BannerLines,
        [string]$Level
    )
    $ansiOne = Format-AnsiLine -Line $OneLiner -Level 'Verdict'
    $ansiBanner = ($BannerLines | ForEach-Object { Format-AnsiLine -Line $_ -Level $Level }) -join [Environment]::NewLine
    $topBlock = $ansiOne + [Environment]::NewLine +
        [Environment]::NewLine + [Environment]::NewLine + [Environment]::NewLine +
        $ansiBanner + [Environment]::NewLine
    $existing = [System.IO.File]::ReadAllText($logPath)
    [System.IO.File]::WriteAllText($logPath, $topBlock + $existing, [System.Text.UTF8Encoding]::new($false))
}

function Write-LogHeader {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss K'
    $divider = ('=' * 78)
    @(
        $divider,
        'Fortnite Video Software Build Log',
        "Started : $stamp",
        "Machine : $env:COMPUTERNAME",
        "User    : $env:USERNAME",
        "Root    : $root",
        "Script  : $batchPath",
        "Log file: $logPath",
        '(Quick verdict: first line of this file after build - OK / WARN / FAIL)',
        '(ANSI colors in log: red=error, yellow=warning, green=success)',
        $divider,
        ''
    ) | ForEach-Object { Write-LoggedLine $_ }
}

function Write-LogFooter {
    param([int]$ExitCode, [TimeSpan]$Duration, [hashtable]$Verdict)
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss K'
    $divider = ('=' * 78)
    $banner = New-VerdictBannerLines -VerdictLabel $Verdict.Label -ExitCode $ExitCode -Duration $Duration -Placement 'bottom'
    $banner | ForEach-Object { Write-LoggedLine $_ -ForceLevel $Verdict.Level }
    @(
        $divider,
        "Finished: $stamp",
        $divider,
        ''
    ) | ForEach-Object { Write-LoggedLine $_ -ForceLevel 'Banner' }
}

function Convert-PipelineLine {
    param($Item)
    if ($null -eq $Item) { return $null }
    if ($Item -is [System.Management.Automation.ErrorRecord]) {
        return $Item.ToString().Trim()
    }
    return $Item.ToString()
}

if (Test-Path -LiteralPath $logPath) {
    Remove-Item -LiteralPath $logPath -Force
}
if (Test-Path -LiteralPath $legacyResultPath) {
    Remove-Item -LiteralPath $legacyResultPath -Force
}

$started = Get-Date
Write-LogHeader



Push-Location $repoRoot
try {
    $cmdArgs = @('/d', '/c', $batchPath, '__BUILD_LOGGED__') + @($BatchArgs)
    & cmd.exe @cmdArgs 2>&1 | ForEach-Object {
        $line = Convert-PipelineLine $_
        if (-not [string]::IsNullOrEmpty($line)) {
            Write-LoggedLine $line
        }
    }
    $exitCode = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
}
finally {
    Pop-Location
}

$duration = (Get-Date) - $started
$verdict = Get-BuildVerdict -ExitCode $exitCode
$oneLiner = New-BuildSummaryOneLiner -Verdict $verdict -ExitCode $exitCode -Duration $duration
$topBanner = New-VerdictBannerLines -VerdictLabel $verdict.Label -ExitCode $exitCode -Duration $duration -Placement 'top'

Prepend-BuildLogSummary -OneLiner $oneLiner -BannerLines $topBanner -Level $verdict.Level
Write-VerdictToConsole -OneLiner $oneLiner -Lines $topBanner -Level $verdict.Level
Write-LogFooter -ExitCode $exitCode -Duration $duration -Verdict $verdict

Pop-Location

exit $exitCode
