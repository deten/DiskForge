using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine;
using DiskForge.Engine.Operations;
using Serilog;

namespace DiskForge.Cli;

/// <summary>One partition to lay down: filesystem, label, and how much of the disk it gets.</summary>
internal sealed record PlannedPartition(FileSystemType FileSystem, string Label, ulong SizeBytes)
{
    /// <summary>The last entry takes whatever is left, so a plan always fills the disk.</summary>
    public bool TakesRemainder => SizeBytes == 0;
}

/// <summary>
/// Headless multi-filesystem provisioning: erase a disk, put it on GPT, and lay down one partition per
/// Linux filesystem. Built for exercising a Linux-filesystem reader on Windows, where you want every
/// filesystem present on one stick at once.
///
/// DESTRUCTIVE, and gated behind an explicit <c>--yes</c> because there is no dialog to confirm in.
/// It drives the ordinary <see cref="IDiskOperation"/> pipeline — every guard, capability gate and
/// pre-write re-check applies exactly as it does from the GUI.
/// </summary>
internal static class Provision
{
    private const ulong MiB = 1024UL * 1024;

    /// <summary>
    /// Sizes are generous rather than minimal so each mkfs has room for its own metadata layout —
    /// mkfs.xfs refuses below 300 MB and mkfs.btrfs below 128 MiB. f2fs takes the remainder.
    /// </summary>
    private static readonly PlannedPartition[] Plan =
    {
        new(FileSystemType.Ext2, "DF-EXT2", 200 * MiB),
        new(FileSystemType.Ext3, "DF-EXT3", 200 * MiB),
        new(FileSystemType.Ext4, "DF-EXT4", 200 * MiB),
        new(FileSystemType.Btrfs, "DF-BTRFS", 300 * MiB),
        new(FileSystemType.Xfs, "DF-XFS", 400 * MiB),
        new(FileSystemType.F2fs, "DF-F2FS", 0)
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var diskNumber = IntArg(args, "--disk");
        var confirmed = args.Contains("--yes", StringComparer.OrdinalIgnoreCase);

        if (diskNumber is null)
        {
            Console.Error.WriteLine("usage: diskforge provision --disk <n> --yes");
            Console.Error.WriteLine("  Erases disk <n>, converts it to GPT, and creates one partition per");
            Console.Error.WriteLine("  Linux filesystem (ext2, ext3, ext4, btrfs, xfs, f2fs).");
            return 2;
        }

        if (!Elevation.IsElevated())
        {
            Console.Error.WriteLine("Administrator is required to write to a disk. Re-run from an elevated shell.");
            return 3;
        }

        var inspector = new SystemInspector();
        var state = inspector.Capture();
        var disk = state.FindDisk(diskNumber.Value);
        if (disk is null)
        {
            Console.Error.WriteLine($"Disk {diskNumber} was not found.");
            return 4;
        }

        Console.WriteLine();
        Console.WriteLine($"Target: disk {disk.Number} — {disk.FriendlyName} ({Size(disk.SizeBytes)}, " +
                          $"{disk.Bus}, {(disk.IsRemovable ? "removable" : "INTERNAL")}, {disk.PartitionStyle})");
        Console.WriteLine("EVERYTHING ON THIS DISK WILL BE DESTROYED.");

        if (!confirmed)
        {
            Console.Error.WriteLine("Refusing to continue without --yes.");
            return 5;
        }

        // Refuse a plan the disk cannot hold before erasing anything.
        var needed = Plan.Sum(p => (decimal)(p.TakesRemainder ? p.FileSystem.MinimumSizeBytes() : p.SizeBytes));
        if ((decimal)disk.SizeBytes < needed + 64 * MiB)
        {
            Console.Error.WriteLine(
                $"Disk {disk.Number} is {Size(disk.SizeBytes)}; this plan needs at least " +
                $"{Size((ulong)needed + 64 * MiB)}.");
            return 6;
        }

        foreach (var p in Plan)
        {
            if (state.LinuxToolchain.BlockingReason(p.FileSystem) is { } blocked)
            {
                Console.Error.WriteLine($"{p.FileSystem.ToFormatName()}: {blocked}");
                return 7;
            }
        }

        if (await ConvertToGptAsync(inspector, disk.Number) is { } gptError)
        {
            Console.Error.WriteLine(gptError);
            return 8;
        }

        return await CreateAllAsync(inspector, disk.Number).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the disk onto GPT. There is no "initialize only" operation, so this uses the clean-whole-disk
    /// format (which erases, sets the partition table and creates one partition) and then deletes the
    /// partition it made, leaving an empty GPT disk. ext2 is used for that throwaway format because it
    /// is written natively and needs no WSL round-trip.
    /// </summary>
    private static async Task<string?> ConvertToGptAsync(SystemInspector inspector, int diskNumber)
    {
        Step("Erasing the disk and writing a GPT partition table");

        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = diskNumber,
            Scope = FormatScope.CleanWholeDisk,
            PartitionScheme = PartitionSchemeChoice.Gpt,
            FileSystem = FileSystemType.Ext2,
            Label = "DF-TEMP"
        }, inspector);

        if (await RunAsync(format) is { } failure) return "Could not put the disk on GPT: " + failure;

        var after = inspector.Capture(probeLinuxToolchain: false);
        var disk = after.FindDisk(diskNumber);
        if (disk is null) return "The disk disappeared after being erased.";

        if (disk.PartitionStyle != PartitionStyle.Gpt)
            return $"The disk came back as {disk.PartitionStyle}, not GPT. Windows re-initializes " +
                   "removable disks to MBR on its own; this drive will not hold GPT, so six partitions " +
                   "are not possible on it (MBR allows three).";

        Console.WriteLine("    partition table is now GPT");

        // Remove the placeholder so the whole disk is free space again. Reserved partitions that
        // Initialize-Disk creates (MSR) are left alone — they are not ours to delete.
        var placeholder = disk.Partitions.FirstOrDefault(
            p => !p.IsUnallocated && p.Kind is PartitionKind.Linux or PartitionKind.Basic);
        if (placeholder?.PartitionNumber is not { } number) return null;

        Step($"Removing the placeholder partition {number}");
        var delete = new DeletePartitionOperation(new DeletePartitionSettings
        {
            DiskNumber = diskNumber,
            PartitionNumber = number,
            OffsetBytes = placeholder.OffsetBytes
        }, inspector);

        return await RunAsync(delete) is { } deleteFailure
            ? "Could not remove the placeholder partition: " + deleteFailure
            : null;
    }

    /// <summary>Lays the planned partitions into the disk's largest free region, in order.</summary>
    private static async Task<int> CreateAllAsync(SystemInspector inspector, int diskNumber)
    {
        var problems = new List<string>();

        for (var i = 0; i < Plan.Length; i++)
        {
            var planned = Plan[i];

            // Re-read between partitions: each create shifts the free space, and the offsets have to
            // come from what is actually on the disk rather than from arithmetic we did up front.
            var state = inspector.Capture(probeLinuxToolchain: false);
            var disk = state.FindDisk(diskNumber);
            var gap = disk?.Partitions
                .Where(p => p.IsUnallocated)
                .OrderByDescending(p => p.SizeBytes)
                .FirstOrDefault();

            if (disk is null || gap is null)
            {
                problems.Add($"{planned.Label}: no unallocated space left on disk {diskNumber}.");
                break;
            }

            var offset = AlignUp(gap.OffsetBytes);
            var available = gap.EndBytes > offset ? gap.EndBytes - offset : 0;
            var size = planned.TakesRemainder ? AlignDown(available) : planned.SizeBytes;

            if (size > available || size == 0)
            {
                problems.Add($"{planned.Label}: needs {Size(size)} but only {Size(available)} is free.");
                break;
            }

            Step($"[{i + 1}/{Plan.Length}] {planned.FileSystem.ToFormatName()} \"{planned.Label}\" " +
                 $"— {Size(size)} at {Size(offset)}");

            var create = new CreatePartitionOperation(new CreatePartitionSettings
            {
                DiskNumber = diskNumber,
                OffsetBytes = offset,
                SizeBytes = size,
                FileSystem = planned.FileSystem,
                Label = planned.Label,
                FormatNew = true,
                DriveLetter = null      // Windows cannot mount any of these
            }, inspector);

            if (await RunAsync(create) is { } failure)
            {
                problems.Add($"{planned.Label}: {failure}");
                break;   // the layout is now unpredictable; stop rather than guess
            }
        }

        Console.WriteLine();
        if (problems.Count == 0)
        {
            Console.WriteLine($"All {Plan.Length} partitions created and verified.");
            return 0;
        }

        Console.Error.WriteLine("Problems:");
        foreach (var problem in problems) Console.Error.WriteLine("  " + problem);
        return 1;
    }

    /// <summary>Validate → Execute → Verify for one operation. Returns null on success, or the reason.</summary>
    private static async Task<string?> RunAsync(IDiskOperation op)
    {
        var progress = new Progress<ApplyProgress>(a =>
            Console.WriteLine($"      {a.Step.Fraction,6:P0}  {a.Step.Step}"));

        var results = await new OperationExecutor()
            .ApplyAsync(new[] { op }, progress, CancellationToken.None)
            .ConfigureAwait(false);

        var result = results.SingleOrDefault();
        if (result is null) return "the operation did not run.";
        if (!result.Success) return result.Error ?? "unknown failure.";
        if (result.Verify is { Verified: false } verify)
            return "completed but verification failed: " + string.Join(" ", verify.Findings);
        return null;
    }

    private static void Step(string text)
    {
        Console.WriteLine();
        Console.WriteLine("==> " + text);
    }

    private static int? IntArg(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
            ? value
            : null;
    }

    private static ulong AlignUp(ulong value)
    {
        var a = CreatePartitionOperation.Alignment;
        var rem = value % a;
        return rem == 0 ? value : value + (a - rem);
    }

    private static ulong AlignDown(ulong value) => value - value % CreatePartitionOperation.Alignment;

    private static string Size(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
