using System.Diagnostics;
using System.Text;
using DiskForge.Engine.Operations;
using Serilog;

namespace DiskForge.Engine.Linux;

/// <summary>
/// Thin process wrapper around <c>wsl.exe</c>.
///
/// Two details bite here and are handled once, centrally:
/// <list type="bullet">
/// <item>wsl.exe emits its own management output (<c>--list</c>, <c>--mount</c>, errors) as UTF-16LE
/// on most builds, while output from a Linux process is passed through as raw UTF-8. We capture raw
/// bytes and sniff, rather than guessing an encoding.</item>
/// <item>Every command is passed as an argv list and run with no shell, so a volume label can never
/// be interpreted as shell syntax. Callers that need shell logic pass a fixed, data-free script.</item>
/// </list>
/// </summary>
internal static class WslCli
{
    /// <summary>Resolved from System32 rather than PATH — this process runs elevated on write paths.</summary>
    public static string ExePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    public static bool IsInstalled => File.Exists(ExePath);

    /// <summary>Windows device path WSL uses to attach a whole physical disk.</summary>
    public static string PhysicalDrivePath(int diskNumber) => $"\\\\.\\PHYSICALDRIVE{diskNumber}";

    /// <summary>Runs wsl.exe with the given arguments. <paramref name="timeout"/> null = wait forever.</summary>
    public static async Task<ShellResult> RunAsync(
        IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Asks modern WSL builds for UTF-8 management output; the sniffer below covers older ones.
        psi.Environment["WSL_UTF8"] = "1";

        Log.Information("wsl.exe {Args}", string.Join(' ', args));

        using var timeoutCts = timeout is { } t ? new CancellationTokenSource(t) : null;
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // mkfs tools prompt when they find an existing filesystem. We always pass a force flag, but
        // closing stdin guarantees a prompt can only ever abort — never hang waiting for input.
        process.StandardInput.Close();

        using var outBuf = new MemoryStream();
        using var errBuf = new MemoryStream();
        var pumpOut = process.StandardOutput.BaseStream.CopyToAsync(outBuf, linked.Token);
        var pumpErr = process.StandardError.BaseStream.CopyToAsync(errBuf, linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(pumpOut, pumpErr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var reason = ct.IsCancellationRequested ? "cancelled" : $"timed out after {timeout!.Value.TotalSeconds:0}s";
            return new ShellResult(-1, "", $"wsl.exe {reason}.");
        }

        var result = new ShellResult(
            process.ExitCode, Decode(outBuf.ToArray()).Trim(), Decode(errBuf.ToArray()).Trim());

        if (!result.Success)
            Log.Warning("wsl.exe failed ({Code}): {Error}", result.ExitCode,
                result.Error.Length > 0 ? result.Error : result.Output);
        return result;
    }

    /// <summary>
    /// The sbin directories where mkfs, blkid and friends live. <c>wsl --exec</c> runs a command with
    /// no shell and therefore no login PATH, so a bare "mkfs.ext4" fails with ENOENT even though the
    /// binary is installed — this puts the standard directories back.
    /// </summary>
    private const string PathFixup =
        "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH; export PATH; exec \"$0\" \"$@\"";

    /// <summary>Runs a command inside a distro as root, with no shell involved.</summary>
    public static Task<ShellResult> RunInDistroAsync(
        string distro, IReadOnlyList<string> command, CancellationToken ct, TimeSpan? timeout = null)
    {
        var args = new List<string> { "-d", distro, "-u", "root", "--exec" };
        args.AddRange(command);
        return RunAsync(args, ct, timeout);
    }

    /// <summary>
    /// Runs a system tool inside a distro with the sbin directories on PATH. The command still travels
    /// as argv — <c>sh</c> only fixes PATH and then <c>exec</c>s it, so arguments such as a volume label
    /// are never parsed as shell syntax.
    /// </summary>
    public static Task<ShellResult> RunToolAsync(
        string distro, IReadOnlyList<string> command, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (command.Count == 0) throw new ArgumentException("A command is required.", nameof(command));

        // sh -c <script> <arg0> <args…>  →  inside the script, $0 is arg0 and "$@" is the rest.
        var wrapped = new List<string> { "sh", "-c", PathFixup };
        wrapped.AddRange(command);
        return RunInDistroAsync(distro, wrapped, ct, timeout);
    }

    /// <summary>
    /// Runs a fixed shell script inside a distro. Only ever called with constant scripts — anything
    /// user-supplied goes through <see cref="RunInDistroAsync"/> as argv.
    /// </summary>
    public static Task<ShellResult> RunScriptAsync(
        string distro, string script, CancellationToken ct, TimeSpan? timeout = null)
        => RunInDistroAsync(distro, new[] { "sh", "-c", script }, ct, timeout);

    /// <summary>
    /// Decodes wsl.exe output. UTF-16LE text read as bytes is ~50% NULs, which no UTF-8 output ever
    /// is, so the ratio is a reliable discriminator.
    /// </summary>
    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return "";

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        var sample = Math.Min(bytes.Length, 512);
        var zeros = 0;
        for (var i = 0; i < sample; i++) if (bytes[i] == 0) zeros++;

        return zeros * 4 > sample
            ? Encoding.Unicode.GetString(bytes)
            : Encoding.UTF8.GetString(bytes);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }
}
