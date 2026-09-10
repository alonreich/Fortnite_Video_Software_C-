param(
    [Parameter(Mandatory = $true)][string]$Executable
)

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path $PSScriptRoot ('artifacts\native-smoke-' + (Get-Date -Format yyyyMMdd-HHmmss))
$stateRoot = Join-Path $testRoot 'state'
$tempRoot = Join-Path $testRoot 'temp'
$logRoot = Join-Path $testRoot 'logs'
New-Item -ItemType Directory -Path $stateRoot, $tempRoot, $logRoot -Force | Out-Null
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')) + [IO.Path]::DirectorySeparatorChar
if (!$Executable.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The executable must be inside this workspace.'
}

function Invoke-NativeCommand([string]$Command, [string[]]$CommandArguments = @()) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $testRoot
    $start.Environment['FVS_PROGRAMDATA_ROOT'] = $stateRoot
    $start.Environment['FVS_DEV_LOG_DIR'] = $logRoot
    $start.Environment['TMP'] = $tempRoot
    $start.Environment['TEMP'] = $tempRoot
    $start.ArgumentList.Add($Command)
    foreach ($argument in $CommandArguments) { $start.ArgumentList.Add($argument) }
    # CLI commands run first; this flag only skips the unrelated event-log sweep.
    $start.ArgumentList.Add('--merger')
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$Command timed out."
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $testRoot "$Command.stdout.txt"), $output)
        [IO.File]::WriteAllText((Join-Path $testRoot "$Command.stderr.txt"), $errors)
        if ($process.ExitCode -ne 0) { throw "$Command failed: $errors" }
        return $output
    }
    finally { $process.Dispose() }
}

$null = Invoke-NativeCommand 'write-state' @('native-smoke')
$state = (Invoke-NativeCommand 'read-state') | ConvertFrom-Json
if ($state.source -ne 'native-smoke') { throw 'Native state round trip failed.' }
'PASS: NativeAOT state JSON round trip.'

$null = Invoke-NativeCommand 'write-crops-defaults'
$cropFile = Join-Path $stateRoot 'crops_coordinations.conf'
$backup = Get-Content -LiteralPath $cropFile -Raw | ConvertFrom-Json
$backup | Add-Member -NotePropertyName audit_marker -NotePropertyValue 'native-backup'
[IO.File]::WriteAllText("$cropFile.bak2", ($backup | ConvertTo-Json -Depth 50))
[IO.File]::WriteAllText("$cropFile.bak1", '{broken')
[IO.File]::WriteAllText($cropFile, '{broken')
$before = (Get-FileHash -LiteralPath "$cropFile.bak2").Hash
$recovered = (Invoke-NativeCommand 'read-crops') | ConvertFrom-Json
$persisted = Get-Content -LiteralPath $cropFile -Raw | ConvertFrom-Json
if ($recovered.audit_marker -ne 'native-backup' -or $persisted.audit_marker -ne 'native-backup') {
    throw 'Native crop backup recovery failed.'
}
if ((Get-FileHash -LiteralPath "$cropFile.bak2").Hash -ne $before) { throw 'Recovery changed the backup.' }
'PASS: NativeAOT crop recovery skips a damaged backup and preserves the valid backup.'
"Evidence: $testRoot"
