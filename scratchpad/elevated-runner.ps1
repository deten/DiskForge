# Persistent elevated job runner.
#
# WHY: every elevated action (VHDX attach, raw disk writes, wsl --mount) otherwise needs its own UAC
# prompt, which steals focus while you are working. Launch this ONCE, leave it running, and it executes
# queued jobs with no further prompts. Close the window to revoke it - nothing survives it.
#
# Launch (accept one UAC prompt):
#   Start-Process powershell -Verb RunAs -ArgumentList '-NoExit','-NoProfile','-ExecutionPolicy','Bypass','-File','<this file>'
#
# It ONLY runs .ps1 files placed in scratchpad\jobs\ - it does not listen on a port or accept anything
# from outside this folder.
#
# ASCII only (PS 5.1 reads a BOM-less .ps1 as ANSI).

$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$repo    = Split-Path -Parent $root
$jobsDir = Join-Path $root 'jobs'
$doneDir = Join-Path $root 'jobs-done'
$heart   = Join-Path $root 'runner-alive.txt'

foreach ($d in @($jobsDir, $doneDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null }
}

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This window is NOT elevated. Close it and relaunch with -Verb RunAs." -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
Set-Location $repo

Write-Host ""
Write-Host "DiskForge elevated runner" -ForegroundColor Cyan
Write-Host "  repo : $repo"
Write-Host "  jobs : $jobsDir"
Write-Host ""
Write-Host "Leave this window open. Close it (or press Ctrl+C) to revoke elevated access." -ForegroundColor Yellow
Write-Host "Waiting for jobs..."
Write-Host ""

while ($true) {
    # Heartbeat so the caller can tell the runner is alive without prompting for anything.
    "alive $(Get-Date -Format 'o') pid=$PID" | Out-File $heart -Encoding utf8

    $jobs = @(Get-ChildItem -Path $jobsDir -Filter '*.ps1' -ErrorAction SilentlyContinue | Sort-Object CreationTime)
    foreach ($job in $jobs) {
        $name = [IO.Path]::GetFileNameWithoutExtension($job.Name)
        $out  = Join-Path $root ("$name.out.txt")
        $flag = Join-Path $root ("$name.done.txt")

        Write-Host ("[{0}] running {1}" -f (Get-Date -Format 'HH:mm:ss'), $job.Name) -ForegroundColor Green
        Remove-Item $flag -ErrorAction SilentlyContinue

        $stdout = Join-Path $root ("$name.stdout.tmp")
        $stderr = Join-Path $root ("$name.stderr.tmp")

        try {
            # Run each job in its own process so a crash or exit cannot take the runner down.
            $proc = Start-Process powershell `
                -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$($job.FullName)`"" `
                -WorkingDirectory $repo -NoNewWindow -Wait -PassThru `
                -RedirectStandardOutput $stdout -RedirectStandardError $stderr
            $code = $proc.ExitCode
        }
        catch {
            $_ | Out-String | Out-File $stderr -Encoding utf8
            $code = 1
        }

        "=== $name : exit $code : $(Get-Date -Format 'o') ===" | Out-File $out -Encoding utf8
        foreach ($f in @($stdout, $stderr)) {
            if ((Test-Path $f) -and (Get-Item $f).Length -gt 0) {
                Get-Content $f | Out-File $out -Append -Encoding utf8
            }
            Remove-Item $f -ErrorAction SilentlyContinue
        }

        Move-Item $job.FullName (Join-Path $doneDir $job.Name) -Force
        "$code" | Out-File $flag -Encoding utf8
        Write-Host ("[{0}] finished {1} (exit {2})" -f (Get-Date -Format 'HH:mm:ss'), $job.Name, $code)
    }

    Start-Sleep -Milliseconds 750
}
