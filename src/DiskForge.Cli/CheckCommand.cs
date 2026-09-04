using DiskForge.Core.Operations;
using DiskForge.Engine;
using DiskForge.Engine.Operations;

namespace DiskForge.Cli;

/// <summary>
/// Headless filesystem check or repair. Drives the ordinary <see cref="IDiskOperation"/> pipeline, so
/// every guard, the pre-run re-check and the verification pass apply exactly as they do from the GUI.
/// The read-only check runs without --yes; the repair demands it because it dismounts the volume.
/// </summary>
internal static class CheckCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var diskNumber = IntArg(args, "--disk");
        var partitionNumber = IntArg(args, "--partition");
        var repair = args.Contains("--repair", StringComparer.OrdinalIgnoreCase);
        var confirmed = args.Contains("--yes", StringComparer.OrdinalIgnoreCase);
        var allowInternal = args.Contains("--allow-internal", StringComparer.OrdinalIgnoreCase);

        if (diskNumber is null || partitionNumber is null)
        {
            Console.Error.WriteLine("usage: diskforge check --disk <n> --partition <p> [--repair --yes] [--allow-internal]");
            Console.Error.WriteLine("  Runs chkdsk read-only and prints its report.");
            Console.Error.WriteLine("  --repair runs chkdsk /f instead: the volume is dismounted for the duration. Needs --yes.");
            Console.Error.WriteLine("  --allow-internal is required to repair a volume on a non-removable disk.");
            return 2;
        }

        if (!Elevation.IsElevated())
        {
            Console.Error.WriteLine("Administrator is required to run chkdsk.");
            return 3;
        }

        var inspector = new SystemInspector();
        var state = inspector.Capture(probeLinuxToolchain: false);
        var part = state.FindDisk(diskNumber.Value)?.Partitions
            .FirstOrDefault(p => p.PartitionNumber == partitionNumber.Value);

        if (part is null)
        {
            Console.Error.WriteLine($"Partition {partitionNumber} was not found on disk {diskNumber}.");
            return 4;
        }

        var op = new CheckFilesystemOperation(new CheckFilesystemSettings
        {
            DiskNumber = diskNumber.Value,
            PartitionNumber = partitionNumber.Value,
            OffsetBytes = part.OffsetBytes,
            DriveLetter = part.DriveLetter,
            Repair = repair,
            AllowNonRemovable = allowInternal
        }, inspector);

        Console.WriteLine();
        Console.WriteLine(op.Describe().Title);
        Console.WriteLine($"  filesystem: {part.Volume?.FileSystem ?? "(none)"}   label: \"{part.Volume?.Label}\"");

        var validation = op.Validate(state);
        if (!validation.IsValid)
        {
            Console.Error.WriteLine();
            foreach (var e in validation.Errors) Console.Error.WriteLine("  refused: " + e);
            return 5;
        }
        foreach (var w in validation.Warnings) Console.WriteLine("  warning: " + w);

        if (repair && !confirmed)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("A repair dismounts the volume. Re-run with --yes to proceed.");
            return 6;
        }

        Console.WriteLine();
        var progress = new Progress<ApplyProgress>(p =>
            Console.WriteLine($"  {p.Step.Fraction,6:P0}  {p.Step.Step}"));

        var results = await new OperationExecutor()
            .ApplyAsync(new[] { (IDiskOperation)op }, progress, CancellationToken.None)
            .ConfigureAwait(false);

        var result = results.Single();

        if (result.Report is { Length: > 0 } report)
        {
            Console.WriteLine();
            Console.WriteLine("--- chkdsk ---");
            Console.WriteLine(report);
            Console.WriteLine("--------------");
        }

        Console.WriteLine();
        if (!result.Success)
        {
            Console.Error.WriteLine("FAILED: " + result.Error);
            return 1;
        }

        if (result.Verify is { Verified: false } verify)
        {
            Console.Error.WriteLine(string.Join(" ", verify.Findings));
            return 1;
        }

        Console.WriteLine(repair ? "Repaired: chkdsk reports the volume is consistent." : "Clean: chkdsk found no problems.");
        return 0;
    }

    private static int? IntArg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : null;
    }
}
