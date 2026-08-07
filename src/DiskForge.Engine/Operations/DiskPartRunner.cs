using System.Diagnostics;
using System.Text;
using Serilog;

namespace DiskForge.Engine.Operations;

/// <summary>
/// Runs a diskpart script. diskpart performs clean → create → format atomically, avoiding the
/// StorageWMI "Not enough available capacity" race that bites New-Partition right after Clear-Disk.
/// Requires elevation (checked by the caller). Success is confirmed by the operation's VerifyAsync.
/// </summary>
public static class DiskPartRunner
{
    public static async Task<ShellResult> RunAsync(string script, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"diskforge-dp-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(scriptPath, script, ct).ConfigureAwait(false);
        Log.Information("diskpart script:\n{Script}", script);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add(scriptPath);

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var output = stdout.ToString();
            // diskpart often returns exit code 0 even on script errors, so also scan its output.
            var failed = process.ExitCode != 0 || MentionsError(output);
            var result = new ShellResult(failed ? 1 : 0, output.Trim(), stderr.ToString().Trim());
            if (failed) Log.Warning("diskpart reported a problem: {Output}", output);
            return result;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        }
    }

    private static bool MentionsError(string output)
    {
        var lower = output.ToLowerInvariant();
        return lower.Contains("has encountered an error")
            || lower.Contains("virtual disk service error")
            || lower.Contains("not enough")
            || lower.Contains("the arguments specified")
            || lower.Contains("is not valid")
            || lower.Contains("no usable free extent")
            || lower.Contains("cannot ");
    }
}
