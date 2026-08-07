using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Exercises the anti-wrong-disk guards in FormatVolumeOperation.Validate — the safety-critical logic
/// that must never let a format land on the wrong disk. Pure logic, no hardware required.
/// </summary>
public class FormatVolumeOperationTests
{
    private const ulong GB = 1024UL * 1024 * 1024;
    private const ulong MB = 1024UL * 1024;

    // ---------- fixtures ----------

    private static PartitionInfo BasicPartition(
        int number = 1, ulong size = 32 * GB, string fs = "NTFS", string letter = "E", BitLockerInfo? bl = null)
        => new()
        {
            PartitionNumber = number,
            Kind = PartitionKind.Basic,
            OffsetBytes = MB,
            SizeBytes = size,
            DriveLetter = letter,
            Volume = new VolumeInfo
            {
                DriveLetter = letter, Label = "USB", FileSystem = fs,
                SizeBytes = size, FreeBytes = size / 2,
                BitLocker = bl ?? BitLockerInfo.NotEncryptable
            }
        };

    private static PhysicalDiskInfo Disk(
        int number = 3, bool removable = true, bool system = false, ulong size = 32 * GB,
        DriveCapability caps = DriveCapability.Format, params PartitionInfo[] parts)
        => new()
        {
            Number = number,
            FriendlyName = removable ? "SanDisk Ultra USB" : "Internal SSD",
            SizeBytes = size,
            Bus = removable ? StorageBus.Usb : StorageBus.Sata,
            Media = DiskMediaType.Ssd,
            IsRemovable = removable,
            IsSystemDisk = system,
            Capabilities = new DriveCapabilities { Supported = caps },
            Partitions = parts.Length > 0 ? parts : new[] { BasicPartition() }
        };

    private static SystemState State(PhysicalDiskInfo disk, int? systemDisk = null)
        => new() { Disks = new[] { disk }, SystemDiskNumber = systemDisk, IsElevated = true };

    private static FormatVolumeSettings Reformat(
        int disk = 3, int part = 1, FileSystemType fs = FileSystemType.Exfat,
        bool allowInternal = false, string label = "NEW")
        => new()
        {
            DiskNumber = disk, Scope = FormatScope.ReformatPartition, PartitionNumber = part,
            FileSystem = fs, Label = label, AllowNonRemovable = allowInternal
        };

    // ---------- the guards ----------

    [Fact]
    public void HappyPath_RemovableUsb_IsValid_WithDestructiveWarning()
    {
        var op = new FormatVolumeOperation(Reformat());
        var result = op.Validate(State(Disk()));

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("erased"));
    }

    [Fact]
    public void Rejects_SystemDisk_ByFlag()
    {
        var op = new FormatVolumeOperation(Reformat(disk: 2));
        var result = op.Validate(State(Disk(number: 2, removable: false, system: true)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("system/boot disk"));
    }

    [Fact]
    public void Rejects_SystemDisk_ByStateNumber()
    {
        var op = new FormatVolumeOperation(Reformat(disk: 2));
        var result = op.Validate(State(Disk(number: 2, removable: false), systemDisk: 2));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("system/boot"));
    }

    [Fact]
    public void Rejects_InternalDisk_ByDefault()
    {
        var op = new FormatVolumeOperation(Reformat(disk: 1));
        var result = op.Validate(State(Disk(number: 1, removable: false)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("INTERNAL"));
    }

    [Fact]
    public void Allows_InternalDisk_WithOverride_ButWarns()
    {
        var op = new FormatVolumeOperation(Reformat(disk: 1, allowInternal: true));
        var result = op.Validate(State(Disk(number: 1, removable: false)));

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("INTERNAL"));
    }

    [Fact]
    public void Rejects_EfiPartition()
    {
        var efi = new PartitionInfo { PartitionNumber = 1, Kind = PartitionKind.Efi, SizeBytes = 100 * MB, IsSystem = true };
        var disk = Disk(parts: efi);
        var op = new FormatVolumeOperation(Reformat(part: 1));

        var result = op.Validate(State(disk));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("system/EFI/recovery"));
    }

    [Fact]
    public void Rejects_BitLockerProtectedVolume()
    {
        var bl = new BitLockerInfo { Protection = BitLockerProtection.On, Conversion = BitLockerConversion.FullyEncrypted };
        var disk = Disk(parts: BasicPartition(bl: bl));
        var op = new FormatVolumeOperation(Reformat());

        var result = op.Validate(State(disk));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("BitLocker"));
    }

    [Fact]
    public void Rejects_Fat32_Over32Gb()
    {
        var disk = Disk(size: 64 * GB, parts: BasicPartition(size: 64 * GB));
        var op = new FormatVolumeOperation(Reformat(fs: FileSystemType.Fat32));

        var result = op.Validate(State(disk));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("FAT32"));
    }

    [Fact]
    public void Rejects_WhenFormatCapabilityMissing()
    {
        var disk = Disk(caps: DriveCapability.None);
        var op = new FormatVolumeOperation(Reformat());

        var result = op.Validate(State(disk));
        Assert.False(result.IsValid);
        Assert.Equal(DriveCapability.Format, result.MissingCapabilities);
    }

    [Fact]
    public void Rejects_UnknownDisk()
    {
        var op = new FormatVolumeOperation(Reformat(disk: 99));
        var result = op.Validate(State(Disk(number: 3)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not found"));
    }

    [Fact]
    public void Rejects_OverlongLabel()
    {
        var op = new FormatVolumeOperation(Reformat(fs: FileSystemType.Fat32, label: "THIS_LABEL_IS_WAY_TOO_LONG"));
        var disk = Disk(size: 16 * GB, parts: BasicPartition(size: 16 * GB));

        var result = op.Validate(State(disk));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Label"));
    }

    // ---------- generated scripts ----------

    /// <summary>
    /// diskpart wipes; PowerShell does everything after. This replaced the all-diskpart script when the
    /// partition table became selectable: diskpart's <c>convert gpt</c> accepts only an empty MBR disk
    /// and fails on the RAW disk that <c>clean</c> leaves on removable media, so the scheme cannot be
    /// honoured from diskpart at all. New-Partition's "Not enough available capacity" race right after
    /// a clean is handled by the retry loop — the same one the Linux clean path has used successfully
    /// on real hardware.
    /// </summary>
    [Fact]
    public void CleanWholeDisk_WipesWithDiskpart_ThenPartitionsFromPowerShell()
    {
        var settings = Reformat(disk: 3) with
        {
            Scope = FormatScope.CleanWholeDisk, PartitionNumber = null,
            FileSystem = FileSystemType.Exfat, FullFormat = false, Label = "MYUSB"
        };
        var script = new FormatVolumeOperation(settings).PreviewScript();

        Assert.Contains("select disk 3", script);
        Assert.Contains("clean", script);
        Assert.Contains("New-Partition -DiskNumber $n -UseMaximumSize", script);
        Assert.Contains("Format-Volume", script);
        Assert.Contains("'exFAT'", script);
        Assert.Contains("'MYUSB'", script);
        Assert.Contains("-AssignDriveLetter", script);
        // The capacity race is why the retry exists; losing it would reintroduce the original failure.
        Assert.Contains("for ($i = 0; $i -lt 12", script);
    }

    [Fact]
    public void CleanWholeDisk_FullFormat_PassesFullToFormatVolume()
    {
        var settings = Reformat(disk: 3) with
        {
            Scope = FormatScope.CleanWholeDisk, PartitionNumber = null,
            FileSystem = FileSystemType.Ntfs, FullFormat = true, Label = "DATA"
        };
        var script = new FormatVolumeOperation(settings).PreviewScript();

        Assert.Contains("'NTFS'", script);
        Assert.Contains("-Full", script);
    }

    // ---------- partition table selection ----------

    [Fact]
    public void CleanWholeDisk_Automatic_DoesNotForceAScheme()
    {
        var settings = Reformat(disk: 3) with
        {
            Scope = FormatScope.CleanWholeDisk, PartitionNumber = null,
            PartitionScheme = PartitionSchemeChoice.Automatic
        };
        var script = new FormatVolumeOperation(settings).PreviewScript();

        // Initializes only a RAW disk and accepts whatever Windows settles on — no convert loop.
        Assert.DoesNotContain("$want", script);
        Assert.DoesNotContain("convert", script);
    }

    [Theory]
    [InlineData(PartitionSchemeChoice.Gpt, "GPT")]
    [InlineData(PartitionSchemeChoice.Mbr, "MBR")]
    public void CleanWholeDisk_ExplicitScheme_ClearsAndRetriesThenVerifies(
        PartitionSchemeChoice choice, string expected)
    {
        var settings = Reformat(disk: 3) with
        {
            Scope = FormatScope.CleanWholeDisk, PartitionNumber = null, PartitionScheme = choice
        };
        var script = new FormatVolumeOperation(settings).PreviewScript();

        Assert.Contains($"$want = '{expected}'", script);
        Assert.Contains("Initialize-Disk -Number $n -PartitionStyle $want", script);
        // Initialize-Disk alone loses the race on removable media — Windows forced a real USB stick
        // back to MBR on all five attempts. diskpart's `convert` on the resulting EMPTY disk is what
        // actually works, so it must stay in the script.
        Assert.Contains($"$convert = '{expected.ToLowerInvariant()}'", script);
        Assert.Contains("convert $convert", script);
        Assert.Contains("diskpart.exe /s $dp", script);
        // The script must not assume success — it has to check and fail loudly.
        Assert.Contains("if ($style -ne $want) { throw", script);
    }

    [Fact]
    public void SchemeChoice_IsRejectedForAReformat()
    {
        var settings = Reformat(disk: 3) with { PartitionScheme = PartitionSchemeChoice.Gpt };
        var result = new FormatVolumeOperation(settings).Validate(State(Disk()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("only be chosen when erasing the whole disk"));
    }

    [Fact]
    public void Mbr_IsRejectedOnADiskItCannotAddress()
    {
        var settings = Reformat(disk: 3) with
        {
            Scope = FormatScope.CleanWholeDisk, PartitionNumber = null,
            PartitionScheme = PartitionSchemeChoice.Mbr, FileSystem = FileSystemType.Ntfs
        };
        // Removable so the internal-disk safety gate doesn't short-circuit before the scheme check.
        var result = new FormatVolumeOperation(settings).Validate(State(Disk(size: 4096UL * GB)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("2 TiB"));
    }

    [Fact]
    public void Gpt_OnRemovableMedia_WarnsThatWindowsMayRevertIt()
    {
        var settings = Reformat(disk: 3) with
        {
            Scope = FormatScope.CleanWholeDisk, PartitionNumber = null,
            PartitionScheme = PartitionSchemeChoice.Gpt
        };
        var result = new FormatVolumeOperation(settings).Validate(State(Disk(removable: true)));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        // Prior real-hardware finding: Windows re-initializes a cleaned USB stick as MBR by itself.
        Assert.Contains(result.Warnings, w => w.Contains("removable") && w.Contains("MBR"));
    }

    [Fact]
    public void Mbr_WarnsAboutTheThreePartitionCeiling()
    {
        var settings = Reformat(disk: 3) with
        {
            Scope = FormatScope.CleanWholeDisk, PartitionNumber = null,
            PartitionScheme = PartitionSchemeChoice.Mbr
        };
        var result = new FormatVolumeOperation(settings).Validate(State(Disk()));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("extended container"));
    }

    [Fact]
    public void Reformat_UsesFormatVolumeCmdlet()
    {
        var script = new FormatVolumeOperation(Reformat(disk: 3, part: 1, fs: FileSystemType.Exfat)).PreviewScript();

        Assert.Contains("Get-Partition -DiskNumber 3 -PartitionNumber 1", script);
        Assert.Contains("Format-Volume", script);
        Assert.Contains("'exFAT'", script);
    }

    [Fact]
    public void CleanWholeDisk_WarnsAllPartitionsErased()
    {
        var disk = Disk(parts: new[] { BasicPartition(1), BasicPartition(2, letter: "F") });
        var settings = Reformat() with { Scope = FormatScope.CleanWholeDisk, PartitionNumber = null };
        var op = new FormatVolumeOperation(settings);

        var result = op.Validate(State(disk));
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("entire disk") || w.Contains("2 partition"));
    }
}
