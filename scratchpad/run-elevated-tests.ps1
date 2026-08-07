# Runs the DiskForge test suite from an elevated shell so the [RequiresElevationFact] and
# [RequiresLinuxToolchainFact] tests actually execute instead of skipping.
#
# Those tests need Administrator for two reasons: attaching the throwaway VHDX loopback disk, and
# `wsl --mount` (handing a physical disk to the WSL2 kernel for a Linux format). Everything they write
# to is a temporary VHDX - never a real drive.
#
# Launch with:
#   Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','<this file>'
#
# ASCII only, deliberately: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so a stray em dash
# here becomes mojibake and breaks the parse.

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$log = Join-Path $PSScriptRoot 'elevated-test-output.txt'

$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"

Set-Location $repo

$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
"=== DiskForge elevated test run - $stamp ===" | Out-File $log -Encoding utf8

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
"Elevated: $elevated" | Out-File $log -Append -Encoding utf8
if (-not $elevated) {
    "ABORT: this script must run elevated." | Out-File $log -Append -Encoding utf8
    exit 1
}

# Record the Linux toolchain the tests will use, so a skip can be told apart from a failure.
# NOTE: no `2>&1` on these native calls. Windows PowerShell 5.1 wraps a native command's stderr lines
# in ErrorRecords, which under $ErrorActionPreference='Stop' aborts the script mid-run and truncates
# the log. stdout/stderr are captured with separate files instead.
"--- wsl -l -v ---" | Out-File $log -Append -Encoding utf8
$env:WSL_UTF8 = '1'
(& wsl.exe -l -v) | Out-File $log -Append -Encoding utf8

$outFile = Join-Path $PSScriptRoot 'elevated-test-stdout.txt'
$errFile = Join-Path $PSScriptRoot 'elevated-test-stderr.txt'

"--- dotnet test ---" | Out-File $log -Append -Encoding utf8
$proc = Start-Process dotnet `
    -ArgumentList 'test', '--nologo', '-v', 'n' `
    -WorkingDirectory $repo -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $outFile -RedirectStandardError $errFile
$code = $proc.ExitCode

if (Test-Path $outFile) { Get-Content $outFile | Out-File $log -Append -Encoding utf8 }
if ((Test-Path $errFile) -and (Get-Item $errFile).Length -gt 0) {
    "--- stderr ---" | Out-File $log -Append -Encoding utf8
    Get-Content $errFile | Out-File $log -Append -Encoding utf8
}

"=== dotnet test exit code: $code ===" | Out-File $log -Append -Encoding utf8
exit $code
