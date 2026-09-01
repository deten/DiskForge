using DiskForge.Core.Operations;
using DiskForge.Engine;
using DiskForge.Engine.Operations;
using DiskForge.Engine.Tests.Harness;

namespace DiskForge.Engine.Tests;

/// <summary>
/// End-to-end write tests for create/delete partition, driven against a throwaway VHDX loopback disk —
/// never a real drive (§7). These exercise the real Execute path (New-Partition / Remove-Partition via
/// PowerShell) plus the real enumeration used by Verify, which unit tests cannot reach.
/// Elevated-only: VHDX attach needs Administrator, so they auto-skip in an unelevated run.
/// </summary>
[Collection(RealDiskCollection.Name)]
public class PartitionWriteOpsVhdxTests
{
    private const ulong MB = 1024UL * 1024;

    private static readonly IProgress<OpProgress> NoProgress = new Progress<OpProgress>();

    /// <summary>A freshly attached VHDX is RAW; give it a GPT table so it is a legal create target.</summary>
    private static async Task InitializeGptAsync(int diskNumber)
    {
        var result = await PowerShellRunner.RunAsync(
            $"$ErrorActionPreference='Stop'; Initialize-Disk -Number {diskNumber} -PartitionStyle GPT",
            CancellationToken.None);
        Assert.True(result.Success, $"Initialize-Disk failed: {result.Error}{result.Output}");
    }

    [RequiresElevationFact]
    public async Task CreatePartition_ThenDelete_RoundTripsOnVhdx()
    {
        using var vhdx = new VhdxLoopbackDisk(256 * MB);
        await InitializeGptAsync(vhdx.DiskNumber);

        var inspector = new SystemInspector();
        var disk = inspector.Capture().FindDisk(vhdx.DiskNumber);
        Assert.NotNull(disk);

        // Take the largest free gap the mapper reports, exactly as the UI does when a user clicks it.
        var gap = disk!.Partitions.Where(p => p.IsUnallocated).OrderByDescending(p => p.SizeBytes).First();

        var create = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = vhdx.DiskNumber,
            OffsetBytes = gap.OffsetBytes,
            SizeBytes = 64 * MB,
            FileSystem = FileSystemType.Ntfs,
            Label = "DFTEST",
            // A VHDX presents as a fixed (non-removable) disk, so the internal-disk gate applies.
            AllowNonRemovable = true
        });

        Assert.True(create.Validate(inspector.Capture()).IsValid);
        Assert.True(create.Simulate(inspector.Capture()).Feasible);

        var createResult = await create.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(createResult.Success, createResult.Error);
        Assert.True((await create.VerifyAsync()).Verified, "Create should verify: partition present and NTFS.");

        // The new partition must be real in the enumeration path, not just reported by the op.
        // NOTE: Initialize-Disk -PartitionStyle GPT leaves a Microsoft Reserved (MSR) partition on the
        // disk, so "the only non-unallocated partition" is NOT a safe assumption — match by our label.
        var afterCreate = inspector.Capture().FindDisk(vhdx.DiskNumber)!;
        var created = afterCreate.Partitions.Single(p => p.Volume?.Label == "DFTEST");
        Assert.Equal("NTFS", created.Volume?.FileSystem);

        // ---- now delete it again ----
        var delete = new DeletePartitionOperation(new DeletePartitionSettings
        {
            DiskNumber = vhdx.DiskNumber,
            PartitionNumber = created.PartitionNumber!.Value,
            OffsetBytes = created.OffsetBytes,
            AllowNonRemovable = true
        });

        Assert.True(delete.Validate(inspector.Capture()).IsValid);

        var deleteResult = await delete.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(deleteResult.Success, deleteResult.Error);
        Assert.True((await delete.VerifyAsync()).Verified, "Delete should verify: nothing left at that offset.");

        // The created partition must be gone; the MSR from GPT init legitimately remains.
        var afterDelete = inspector.Capture().FindDisk(vhdx.DiskNumber)!;
        Assert.DoesNotContain(afterDelete.Partitions,
            p => !p.IsUnallocated && p.OffsetBytes == created.OffsetBytes);
        Assert.DoesNotContain(afterDelete.Partitions, p => p.Volume?.Label == "DFTEST");
    }

    [RequiresElevationFact]
    public async Task CreatePartition_OverlappingExisting_IsRefusedBeforeWriting()
    {
        using var vhdx = new VhdxLoopbackDisk(256 * MB);
        await InitializeGptAsync(vhdx.DiskNumber);

        var inspector = new SystemInspector();
        var gap = inspector.Capture().FindDisk(vhdx.DiskNumber)!
            .Partitions.Where(p => p.IsUnallocated).OrderByDescending(p => p.SizeBytes).First();

        var first = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = vhdx.DiskNumber,
            OffsetBytes = gap.OffsetBytes,
            SizeBytes = 64 * MB,
            FileSystem = FileSystemType.Ntfs,
            Label = "DFTEST",
            AllowNonRemovable = true
        });
        Assert.True((await first.ExecuteAsync(NoProgress, CancellationToken.None)).Success);

        // Same extent again: the freshly-occupied space must now be refused by the extent gate,
        // and — critically — refused by the pre-write re-check inside ExecuteAsync too.
        var overlapping = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = vhdx.DiskNumber,
            OffsetBytes = gap.OffsetBytes,
            SizeBytes = 64 * MB,
            FileSystem = FileSystemType.Ntfs,
            Label = "CLOBBER",
            AllowNonRemovable = true
        });

        Assert.False(overlapping.Validate(inspector.Capture()).IsValid);

        var result = await overlapping.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Preflight re-check failed", result.Error);

        // The original partition must be untouched and no second (CLOBBER) partition created.
        var after = inspector.Capture().FindDisk(vhdx.DiskNumber)!;
        Assert.Contains(after.Partitions, p => p.Volume?.Label == "DFTEST");
        Assert.DoesNotContain(after.Partitions, p => p.Volume?.Label == "CLOBBER");
    }
}
