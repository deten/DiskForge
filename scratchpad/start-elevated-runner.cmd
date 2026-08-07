@echo off
REM Double-click this (or run it from any shell) to start the persistent elevated job runner.
REM You get ONE UAC prompt; the window then stays open and runs queued jobs with no further prompts.
REM Close that window to revoke elevated access - nothing is left behind.
REM
REM WHY -EncodedCommand: this repo sits under "Jobs (S3)\00 Misc\...", a path with spaces AND
REM parentheses, and two separate things mangle it:
REM   1. Start-Process joins -ArgumentList entries with spaces and does NOT quote them, so passing the
REM      script path as an argument tears it into fragments.
REM   2. -Verb RunAs elevates via ShellExecute, which IGNORES -WorkingDirectory and starts the process
REM      in system32 - so a bare relative filename does not resolve either.
REM Base64-encoding the command sidesteps both: the blob contains no spaces or quotes at all, and it
REM carries its own absolute Set-Location.

setlocal
net session >nul 2>&1
if %errorlevel% equ 0 (
  REM Already elevated - run it directly, no prompt needed.
  powershell -NoProfile -ExecutionPolicy Bypass -NoExit -File "%~dp0elevated-runner.ps1"
  goto :eof
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "$s='%~dp0elevated-runner.ps1'; $d=Split-Path -Parent $s; $c=('Set-Location -LiteralPath ''{0}''; & ''{1}''' -f $d,$s); $b=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($c)); Start-Process powershell -Verb RunAs -ArgumentList '-NoExit','-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$b"
