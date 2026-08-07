# Runs the real-hardware ext4 test against ONE nominated removable disk.
# The user authorised disk 3 (General UDisk, ~1.86 GB, currently F:) as a test platform.
# This ERASES that disk. It refuses to run against anything that is not removable.
# ASCII only (PS 5.1 reads a BOM-less .ps1 as ANSI).

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$log = Join-Path $PSScriptRoot 'usb-test-output.txt'
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"

Set-Location $repo
"=== USB ext4 test - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" | Out-File $log -Encoding utf8

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    "ABORT: must run elevated." | Out-File $log -Append -Encoding utf8
    exit 1
}

# Safety gate: only ever a small removable disk.
$disk = Get-Disk -Number 3
"target: #$($disk.Number) '$($disk.FriendlyName)' bus=$($disk.BusType) size=$($disk.Size)" |
    Out-File $log -Append -Encoding utf8

$cim = Get-CimInstance Win32_DiskDrive | Where-Object Index -eq 3
if ($cim.MediaType -ne 'Removable Media') {
    "ABORT: disk 3 is '$($cim.MediaType)', not removable. Refusing." | Out-File $log -Append -Encoding utf8
    exit 2
}
if ($disk.Size -gt 128GB) {
    "ABORT: disk 3 is larger than 128GB. Refusing." | Out-File $log -Append -Encoding utf8
    exit 3
}

$env:DISKFORGE_TEST_DISK = '3'

$outFile = Join-Path $PSScriptRoot 'usb-test-stdout.txt'
$errFile = Join-Path $PSScriptRoot 'usb-test-stderr.txt'

$proc = Start-Process dotnet `
    -ArgumentList 'test', '--nologo', '-v', 'n', '--filter', 'FullyQualifiedName~RealRemovableDiskTests' `
    -WorkingDirectory $repo -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $outFile -RedirectStandardError $errFile
$code = $proc.ExitCode

if (Test-Path $outFile) { Get-Content $outFile | Out-File $log -Append -Encoding utf8 }
if ((Test-Path $errFile) -and (Get-Item $errFile).Length -gt 0) {
    "--- stderr ---" | Out-File $log -Append -Encoding utf8
    Get-Content $errFile | Out-File $log -Append -Encoding utf8
}

"--- resulting disk state ---" | Out-File $log -Append -Encoding utf8
Get-Disk -Number 3 | Format-List Number, FriendlyName, PartitionStyle, OperationalStatus, IsOffline |
    Out-File $log -Append -Encoding utf8
Get-Partition -DiskNumber 3 -ErrorAction SilentlyContinue |
    Format-Table PartitionNumber, Offset, Size, DriveLetter, GptType -AutoSize |
    Out-File $log -Append -Encoding utf8

"=== exit code: $code ===" | Out-File $log -Append -Encoding utf8
exit $code
