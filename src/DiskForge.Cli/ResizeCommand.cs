using DiskForge.Core.Operations;
using DiskForge.Engine;
using DiskForge.Engine.Operations;

namespace DiskForge.Cli;

/// <summary>
/// Headless partition resize. Drives the ordinary <see cref="IDiskOperation"/> pipeline, so every
/// guard, the pre-write re-check and the verification pass all apply exactly as they do from the GUI.
/// </summary>
internal static class ResizeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var diskNumber = IntArg(args, "--disk");
        var partitionNumber = IntArg(args, "--partition");
        var sizeMb = IntArg(args, "--size");
        var confirmed = args.Contains("--yes", StringComparer.OrdinalIgnoreCase);
        var allowInternal = args.Contains("--allow-internal", StringComparer.OrdinalIgnoreCase);

        if (diskNumber is null || partitionNumber is null || sizeMb is null)
        {
            Console.Error.WriteLine("usage: diskforge resize --disk <n> --partition <p> --size <MB> --yes");
            Console.Error.WriteLine("  Grows or shrinks a partition, keeping its contents.");
            Console.Error.WriteLine("  NTFS grows and shrinks; ext2/ext3/ext4 grow only.");
            Console.Error.WriteLine("  --allow-internal is required to touch a non-removable disk.");
            return 2;
        }

        if (!Elevation.IsElevated())
        {
            Console.Error.WriteLine("Administrator is required to resize a partition.");
            return 3;
        }

        var inspector = new SystemInspector();
        var state = inspector.Capture();
        var part = state.FindDisk(diskNumber.Value)?.Partitions
            .FirstOrDefault(p => p.PartitionNumber == partitionNumber.Value);

        if (part is null)
        {
            Console.Error.WriteLine($"Partition {partitionNumber} was not found on disk {diskNumber}.");
            return 4;
        }

        var newSize = (ulong)sizeMb.Value * 1024 * 1024;
        var op = new ResizePartitionOperation(new ResizePartitionSettings
        {
            DiskNumber = diskNumber.Value,
            PartitionNumber = partitionNumber.Value,
            NewSizeBytes = newSize,
            OffsetBytes = part.OffsetBytes,
            CurrentSizeBytes = part.SizeBytes,
            DriveLetter = part.DriveLetter,
            AllowNonRemovable = allowInternal
        }, inspector);

        var describe = op.Describe();
        Console.WriteLine();
        Console.WriteLine(describe.Title);
        Console.WriteLine($"  current: {Size(part.SizeBytes)}   target: {Size(newSize)}");
        Console.WriteLine($"  filesystem: {part.Volume?.FileSystem ?? "(none)"}");

        var validation = op.Validate(state);
        if (!validation.IsValid)
        {
            Console.Error.WriteLine();
            foreach (var e in validation.Errors) Console.Error.WriteLine("  refused: " + e);
            return 5;
        }
        foreach (var w in validation.Warnings) Console.WriteLine("  warning: " + w);

        var simulation = op.Simulate(state);
        Console.WriteLine();
        foreach (var (step, i) in simulation.PlannedSteps.Select((s, i) => (s, i)))
            Console.WriteLine($"  {i + 1}. {step}");

        if (!confirmed)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Re-run with --yes to apply.");
            return 6;
        }

        Console.WriteLine();
        var progress = new Progress<ApplyProgress>(p =>
            Console.WriteLine($"  {p.Step.Fraction,6:P0}  {p.Step.Step}"));

        var results = await new OperationExecutor()
            .ApplyAsync(new[] { (IDiskOperation)op }, progress, CancellationToken.None)
            .ConfigureAwait(false);

        var result = results.Single();
        Console.WriteLine();
        if (!result.Success)
        {
            Console.Error.WriteLine("FAILED: " + result.Error);
            return 1;
        }

        Console.WriteLine("Resized.");
        if (result.Verify is { Verified: false } verify)
        {
            Console.Error.WriteLine("Verification warned: " + string.Join(" ", verify.Findings));
            return 1;
        }
        return 0;
    }

    private static int? IntArg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : null;
    }

    private static string Size(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
