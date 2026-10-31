#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RclonePath,
    [string]$Python = 'python',
    [ValidateSet('configuration', 'remote-name', 'ui', 'backup-integration', 'rename-compatibility', 'remote-denied', 'local-permissions', 'remote-permissions', 'mixed-permissions', 'audit-2.2.1')]
    [string[]]$Cases = @('configuration', 'remote-name', 'ui', 'backup-integration', 'rename-compatibility', 'remote-denied', 'local-permissions', 'remote-permissions', 'mixed-permissions', 'audit-2.2.1'),
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'src\MagicTray'
$testRoot = Join-Path $repoRoot ('_tooltest\repro-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$rcloneExe = (Resolve-Path -LiteralPath $RclonePath).Path
$env:MAGICTRAY_TEST_RCLONE = $rcloneExe
$pythonExe = (Get-Command $Python -ErrorAction Stop).Source
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$dependencyRoot = Join-Path $repoRoot '_tooltest\deps'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Install .NET Framework with the Windows C# compiler first.' }
if (-not (Test-Path -LiteralPath (Join-Path $dependencyRoot 'pyftpdlib'))) {
    throw 'Install the test dependency first: python -m pip install --target .\_tooltest\deps pyftpdlib==2.1.0'
}
New-Item -ItemType Directory -Path $testRoot | Out-Null
$sources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' | ForEach-Object { $_.FullName })
$utf8 = New-Object System.Text.UTF8Encoding($false)
$results = New-Object System.Collections.Generic.List[object]

function Compile-Fixture([string]$fixture, [string]$entryPoint) {
    $fixturePath = Join-Path $sourceRoot ($fixture + '.cs.fixture')
    $generatedPath = Join-Path $testRoot ($fixture + '.cs')
    # Compile an isolated copy; child fixtures read only the test rclone variable.
    $contents = [IO.File]::ReadAllText($fixturePath)
    [IO.File]::WriteAllText($generatedPath, $contents, $utf8)
    $exePath = Join-Path $testRoot ($fixture + '.exe')
    & $compiler /nologo /target:exe /optimize+ ('/main:' + $entryPoint) ('/out:' + $exePath) /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll /r:System.Web.Extensions.dll @sources $generatedPath | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw ('Fixture compilation failed: ' + $fixture) }
    & (Join-Path $env:WINDIR 'System32\icacls.exe') $exePath /setintegritylevel M | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Run tests from a normal desktop PowerShell so generated executables can use Medium integrity.' }
    return $exePath
}

function Quote-Args([string[]]$values) {
    return (($values | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' ')
}

function Run-Case([string]$name, [string]$exe, [string]$serverMode, [string[]]$extra, [switch]$NoServer) {
    if ($Cases -notcontains $name) { return }
    $caseRoot = Join-Path $testRoot $name
    $run = Join-Path $caseRoot 'run'
    $ftp = Join-Path $caseRoot 'ftp'
    $portFile = Join-Path $caseRoot 'port.txt'
    New-Item -ItemType Directory -Path $caseRoot, $run, $ftp | Out-Null
    $server = $null
    $testProcess = $null
    try {
        if ($NoServer) {
            $testArgs = @($run) + $extra
        } else {
            $serverArgs = @((Join-Path $sourceRoot 'ftp-test-server.py'), $ftp, $portFile, $serverMode)
            $server = Start-Process -FilePath $pythonExe -ArgumentList (Quote-Args $serverArgs) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $caseRoot 'server.out.log') -RedirectStandardError (Join-Path $caseRoot 'server.err.log')
            for ($attempt = 0; $attempt -lt 100; $attempt++) {
                if (Test-Path -LiteralPath $portFile) { break }
                if ($server.HasExited) { throw ('Local FTP fixture failed; see ' + $caseRoot) }
                Start-Sleep -Milliseconds 100
            }
            if (-not (Test-Path -LiteralPath $portFile)) { throw 'Timed out waiting for the localhost FTP server.' }
            $port = [IO.File]::ReadAllText($portFile).Trim()
            $testArgs = @($run, $port, $ftp) + $extra
        }
        $testProcess = Start-Process -FilePath $exe -ArgumentList (Quote-Args $testArgs) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $caseRoot 'console.log') -RedirectStandardError (Join-Path $caseRoot 'console.err.log')
        # Cache the handle before the process exits (PowerShell 5.1 Start-Process).
        $processHandle = $testProcess.Handle
        if (-not $testProcess.WaitForExit($TimeoutSeconds * 1000)) {
            $testProcess.Kill()
            $testProcess.WaitForExit()
            throw ('Test timed out: ' + $name)
        }
        $testProcess.Refresh()
        $exitCode = $testProcess.ExitCode
        if ($null -eq $exitCode) { throw ('Cannot read test exit code: ' + $name) }
        $log = Join-Path $run 'test-results.log'
        if (-not (Test-Path -LiteralPath $log)) { $log = Join-Path $caseRoot 'console.log' }
        $summary = Get-Content -LiteralPath $log -Tail 1
        Write-Host ('{0}: exit={1}; {2}' -f $name, $exitCode, $summary)
        $results.Add([pscustomobject]@{ Case = $name; Exit = $exitCode; Result = $summary })
    } finally {
        if ($testProcess) { $testProcess.Dispose() }
        if ($server) {
            if (-not $server.HasExited) { $server.Kill(); $server.WaitForExit() }
            $server.Dispose()
        }
    }
}

$config = Compile-Fixture 'ConfigPerformanceChecks' 'ConfigPerformanceChecks'
$rename = Compile-Fixture 'RenameChecks' 'RenameChecks'
$ui = Compile-Fixture 'UiCheck' 'MagicTray.UiCheck'
$integration = Compile-Fixture 'Tests' 'MagicTray.IntegrationTests'
$compatibility = Compile-Fixture 'BackupCompatibilityTests' 'BackupCompatibilityTests'
$skip = Compile-Fixture 'SkipTests' 'SkipTests'
$audit = Compile-Fixture 'AuditTests' 'AuditTests'

Run-Case 'configuration' $config '' @() -NoServer
Run-Case 'remote-name' $rename '' @($rcloneExe) -NoServer
Run-Case 'ui' $ui '' @() -NoServer
Run-Case 'backup-integration' $integration '' @('backup-only')
Run-Case 'rename-compatibility' $compatibility 'reject-partial' @('partial')
Run-Case 'remote-denied' $compatibility 'reject-store' @('denied')
Run-Case 'local-permissions' $skip '' @('normal')
Run-Case 'remote-permissions' $skip 'reject-store' @('remote')
Run-Case 'mixed-permissions' $skip 'reject-selected-store' @('mixed')
Run-Case 'audit-2.2.1' $audit 'reject-selected-store' @($rcloneExe)

Write-Host ('Results: ' + $testRoot)
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'summary.json') -Encoding UTF8
if (@($results | Where-Object { $_.Exit -ne 0 }).Count -gt 0) { exit 1 }
Write-Host 'All local cases passed. No real-phone configuration was used.'
