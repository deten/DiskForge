using System.Diagnostics;
using System.Text;
using Serilog;

namespace DiskForge.Engine.Operations;

public sealed record ShellResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Executes a PowerShell script via the built-in Storage cmdlets (Format-Volume, Clear-Disk,
/// New-Partition, …). These cmdlets encapsulate the multi-step orchestration and edge cases far more
/// safely than hand-rolled CIM calls. The child process inherits the parent's elevation.
/// </summary>
public static class PowerShellRunner
{
    public static async Task<ShellResult> RunAsync(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        Log.Information("PowerShell op: {Script}", script);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var result = new ShellResult(process.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
        if (!result.Success)
            Log.Warning("PowerShell op failed ({Code}): {Error}", result.ExitCode, result.Error);
        return result;
    }
}
