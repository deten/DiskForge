using System.Diagnostics;
using System.Text;
using Serilog;

namespace DiskForge.Engine.Operations;

/// <summary>
/// Runs a Windows command-line tool (chkdsk, bcdboot, mbr2gpt) and captures everything it prints.
///
/// Arguments are passed as an argv list, never a shell string, so a volume label or path can never
/// become syntax. Standard input is redirected to nothing: several of these tools stop and ask a
/// question when they hit a condition the caller did not anticipate, and a prompt nobody can answer
/// would hang the operation forever. With no stdin the tool sees end-of-file, gives up, and the
/// caller gets its output and exit code to explain what happened.
/// </summary>
public static class ExternalProcess
{
    public static async Task<ShellResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        Log.Information("External tool: {Tool} {Args}", fileName, string.Join(' ', arguments));

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.StandardInput.Close(); // end-of-file, so a prompt fails instead of waiting
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        var result = new ShellResult(process.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
        if (!result.Success)
            Log.Warning("External tool {Tool} exited {Code}", fileName, result.ExitCode);
        return result;
    }
}
