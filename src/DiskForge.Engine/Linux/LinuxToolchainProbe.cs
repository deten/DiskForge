using System.Diagnostics;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using Serilog;

namespace DiskForge.Engine.Linux;

/// <summary>
/// Read-only probe (no elevation needed) that answers one question honestly: which Linux filesystems
/// can this machine actually write? It enumerates WSL distributions and looks for the real mkfs
/// binaries inside them. Nothing is reported as supported unless the tool was seen — a filesystem
/// with no tool is surfaced with the package that would provide it, never silently offered.
///
/// Enumerating distros is instant; looking inside one boots it, so the tool sweep is budgeted and
/// cached for the process lifetime (<see cref="Refresh"/> re-runs it).
/// </summary>
public static class LinuxToolchainProbe
{
    /// <summary>Filesystems worth probing for, cheapest/most likely first.</summary>
    private static readonly FileSystemType[] Candidates =
    {
        FileSystemType.Ext4, FileSystemType.Ext3, FileSystemType.Ext2,
        FileSystemType.Btrfs, FileSystemType.Xfs, FileSystemType.F2fs,
        FileSystemType.LinuxSwap
    };

    /// <summary>Extra binaries the format/verify path needs beyond mkfs itself.</summary>
    /// <summary>
    /// Tools looked for alongside the mkfs binaries. blkid verifies a format; e2fsck and dumpe2fs are
    /// the independent judges the test suite runs over ext images DiskForge wrote. Recording whether
    /// they were actually seen is what lets a test skip, rather than fail, on a box whose WSL is
    /// installed but will not start.
    /// </summary>
    private static readonly string[] SupportTools = { "blkid", "wipefs", "partprobe", "udevadm", "e2fsck", "dumpe2fs" };

    private static readonly TimeSpan DistroTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(15);

    private static readonly object Gate = new();
    private static LinuxToolchainInfo? _cached;

    /// <summary>Cached toolchain info, probing once on first use.</summary>
    public static LinuxToolchainInfo Get()
    {
        lock (Gate)
        {
            return _cached ??= ProbeCore();
        }
    }

    /// <summary>Discards the cache so the next <see cref="Get"/> re-probes (e.g. after `wsl --install`).</summary>
    public static void Refresh()
    {
        lock (Gate) { _cached = null; }
    }

    /// <summary>The distribution that should run a given filesystem's mkfs, or null when none can.</summary>
    public static string? DistroFor(FileSystemType fs)
    {
        var tool = Get().ToolFor(fs);
        return tool.Available ? tool.Distro : null;
    }

    /// <summary>Any distribution that can run the support tools (blkid) — used by the verify pass.</summary>
    public static string? AnyDistro() =>
        Get().Distros.FirstOrDefault(d => d.IsDefault && d.WslVersion == 2)?.Name
        ?? Get().Distros.FirstOrDefault(d => d.WslVersion == 2)?.Name;

    private static LinuxToolchainInfo ProbeCore()
    {
        var sw = Stopwatch.StartNew();

        if (!WslCli.IsInstalled)
            return new LinuxToolchainInfo
            {
                IsAvailable = false,
                Reason = "WSL is not installed on this machine (wsl.exe was not found)."
            };

        // `wsl --version` only exists on the modern (Store) WSL, which is also the only one with
        // `wsl --mount`. Treating its absence as "no disk passthrough" is a heuristic, and it is
        // reported as such rather than as a hard fact about the machine.
        var version = RunCli(new[] { "--version" });
        var hasModernWsl = version.Success && version.Output.Length > 0;

        var distros = ListDistros();
        if (distros.Count == 0)
            return new LinuxToolchainInfo
            {
                IsAvailable = false,
                VersionText = hasModernWsl ? FirstLine(version.Output) : null,
                Reason = "WSL is present but no Linux distribution is installed " +
                         "(install one with `wsl --install -d Ubuntu`)."
            };

        var wsl2 = distros.Where(d => d.WslVersion == 2).ToList();
        if (wsl2.Count == 0)
            return new LinuxToolchainInfo
            {
                IsAvailable = false,
                Distros = distros,
                VersionText = hasModernWsl ? FirstLine(version.Output) : null,
                Reason = $"Only WSL 1 distributions are installed ({string.Join(", ", distros.Select(d => d.Name))}). " +
                         "Attaching a physical disk needs WSL 2 — convert with `wsl --set-version <distro> 2`."
            };

        var tools = SweepTools(wsl2, sw, out var supportPaths);
        var usable = tools.Where(kv => kv.Value.Available).Select(kv => kv.Key).ToList();
        var host = tools.Values.FirstOrDefault(t => t.Available)?.Distro ?? wsl2[0].Name;

        var info = new LinuxToolchainInfo
        {
            IsAvailable = usable.Count > 0,
            Reason = usable.Count > 0
                ? null
                : "No mkfs tools were found in any WSL distribution " +
                  "(install e2fsprogs / btrfs-progs / xfsprogs inside your distro).",
            BackendName = $"WSL2 ({host})",
            VersionText = hasModernWsl ? FirstLine(version.Output) : null,
            Distros = distros,
            Tools = tools,
            SupportToolPaths = supportPaths,
            SupportsDiskMount = hasModernWsl
        };

        Log.Information(
            "Linux toolchain probe took {Elapsed}ms: {Distros} distro(s), usable filesystems: {Usable}",
            sw.ElapsedMilliseconds, distros.Count,
            usable.Count == 0 ? "(none)" : string.Join(", ", usable.Select(f => f.ToFormatName())));

        return info;
    }

    /// <summary>
    /// Looks for the mkfs binaries, default distro first, stopping as soon as every filesystem is
    /// accounted for or the time budget runs out. Booting a stopped distro costs seconds, so the
    /// common single-distro case pays exactly one boot.
    /// </summary>
    private static Dictionary<FileSystemType, LinuxToolAvailability> SweepTools(
        IReadOnlyList<LinuxDistroInfo> wsl2, Stopwatch sw, out Dictionary<string, string> supportFound)
    {
        var found = new Dictionary<FileSystemType, LinuxToolAvailability>();
        supportFound = new Dictionary<string, string>(StringComparer.Ordinal);

        var order = wsl2
            .OrderByDescending(d => d.IsDefault)
            .ThenByDescending(d => d.IsRunning)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var probedAny = false;

        foreach (var distro in order)
        {
            if (probedAny && sw.Elapsed > SweepBudget)
            {
                Log.Information("Linux toolchain sweep stopped after the {Budget}s budget; " +
                                "{Distro} and later were not probed", SweepBudget.TotalSeconds, distro.Name);
                break;
            }

            var paths = ProbeDistroTools(distro.Name);
            probedAny = true;
            if (paths.Count == 0) continue;

            foreach (var fs in Candidates)
            {
                if (found.ContainsKey(fs) && found[fs].Available) continue;
                var tool = fs.MkfsTool();
                if (tool is not null && paths.TryGetValue(tool, out var path))
                    found[fs] = new LinuxToolAvailability(true, distro.Name, path, null);
            }

            foreach (var support in SupportTools)
                if (paths.TryGetValue(support, out var p) && !supportFound.ContainsKey(support))
                    supportFound[support] = p;

            if (Candidates.All(fs => found.TryGetValue(fs, out var t) && t.Available)) break;
        }

        // Record the misses too, so the UI can explain each one rather than just hiding it.
        foreach (var fs in Candidates)
        {
            if (found.ContainsKey(fs)) continue;
            var probedNames = string.Join(", ", order.Select(d => d.Name));
            found[fs] = new LinuxToolAvailability(false, null, null,
                $"{fs.MkfsTool()} not present in {probedNames}");
        }

        if (!supportFound.ContainsKey("blkid"))
            Log.Warning("blkid was not found in any WSL distribution — Linux format verification will be limited");

        return found;
    }

    /// <summary>Returns tool → absolute path for every candidate binary present in the distro.</summary>
    private static Dictionary<string, string> ProbeDistroTools(string distro)
    {
        var wanted = Candidates
            .Select(fs => fs.MkfsTool())
            .Where(t => t is not null)
            .Concat(SupportTools)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Fixed script over a fixed tool list — no caller-supplied data reaches the shell.
        // PATH is forced because a non-login shell in some distros omits /sbin, where mkfs lives.
        var script =
            "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH; " +
            "for t in " + string.Join(' ', wanted) + "; do " +
            "p=$(command -v \"$t\" 2>/dev/null); [ -n \"$p\" ] && printf '%s %s\\n' \"$t\" \"$p\"; " +
            "done; exit 0";

        var result = RunScript(distro, script);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!result.Success)
        {
            Log.Warning("Could not inspect WSL distro {Distro}: {Error}", distro,
                result.Error.Length > 0 ? result.Error : result.Output);
            return paths;
        }

        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0) paths[parts[0]] = parts[1];
        }
        return paths;
    }

    /// <summary>
    /// Parses <c>wsl --list --verbose</c>. Output is fixed-width-ish and localized, so only the
    /// structure is trusted: an optional leading '*' marks the default, the last column is the
    /// version, and the first column is the name.
    /// </summary>
    internal static IReadOnlyList<LinuxDistroInfo> ParseDistroList(string output)
    {
        var distros = new List<LinuxDistroInfo>();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in lines)
        {
            var line = raw.Replace("\r", "").TrimEnd();
            if (line.Trim().Length == 0) continue;

            var isDefault = line.TrimStart().StartsWith('*');
            var body = line.TrimStart();
            if (isDefault) body = body[1..].TrimStart();

            var cols = body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cols.Length < 3) continue;

            // Header row: the last column is not a number.
            if (!int.TryParse(cols[^1], out var version)) continue;

            var name = cols[0];
            var state = cols[^2];
            distros.Add(new LinuxDistroInfo(
                name, version, isDefault,
                state.Contains("Running", StringComparison.OrdinalIgnoreCase)));
        }

        return distros;
    }

    private static IReadOnlyList<LinuxDistroInfo> ListDistros()
    {
        var result = RunCli(new[] { "--list", "--verbose" });
        if (!result.Success)
        {
            // A machine with WSL installed but no distro exits non-zero here — that is information,
            // not an error worth throwing over.
            Log.Information("wsl --list --verbose returned {Code}: {Output}", result.ExitCode,
                result.Error.Length > 0 ? result.Error : result.Output);
            return Array.Empty<LinuxDistroInfo>();
        }
        return ParseDistroList(result.Output);
    }

    private static Operations.ShellResult RunCli(IReadOnlyList<string> args)
        => WslCli.RunAsync(args, CancellationToken.None, CliTimeout).GetAwaiter().GetResult();

    private static Operations.ShellResult RunScript(string distro, string script)
        => WslCli.RunScriptAsync(distro, script, CancellationToken.None, DistroTimeout)
            .GetAwaiter().GetResult();

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? text;
}
