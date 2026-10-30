#Requires -Version 5.1
<#
.SYNOPSIS
    Rebuild Magic5Tray.exe from Magic5Tray.cs using the .NET Framework compiler.

.DESCRIPTION
    Output goes to the repository root: ..\..\Magic5Tray.exe
    The .ini file is not embedded; keep Magic5Tray.ini next to the exe.
    Restores only the generated executable's normal Medium integrity label.
    Run from a normal desktop PowerShell; administrator rights are not needed.
#>
$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    throw "C# compiler not found: $csc"
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object { $_.FullName })
$output = Join-Path $repoRoot 'Magic5Tray.exe'
$staged = Join-Path $repoRoot 'Magic5Tray.build.exe'
$manifest = Join-Path $PSScriptRoot 'app.manifest'

foreach ($process in @(Get-Process -Name 'Magic5Tray' -ErrorAction SilentlyContinue)) {
    if ($process.Path -and [string]::Equals($process.Path, $output, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Close Magic5Tray before rebuilding its executable.'
    }
}

& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
    /out:"$staged" `
    /win32manifest:"$manifest" `
    /r:System.Windows.Forms.dll `
    /r:System.Drawing.dll `
    /r:System.Security.dll `
    /r:System.Web.Extensions.dll `
    @source

if ($LASTEXITCODE -ne 0) { throw "C# compilation failed ($LASTEXITCODE); previous executable preserved." }

# A Low label inherited from a sandbox workspace also lowers the executable's
# runtime token, which prevents WinFsp from defining a drive. This changes only
# this generated file's label, never directory ACLs, UAC or system policies.
& (Join-Path $env:WINDIR 'System32\icacls.exe') $staged /setintegritylevel M
if ($LASTEXITCODE -ne 0) {
    throw 'Cannot publish a normal-integrity executable. Run this build script from a normal desktop PowerShell; the previous executable is preserved.'
}
Move-Item -LiteralPath $staged -Destination $output -Force
Write-Host "built: $output"

