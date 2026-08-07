using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Linux;
using DiskForge.Engine.Operations;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Gating and script generation for Linux filesystem formats (ext4/btrfs/xfs/f2fs/swap). Pure logic:
/// the toolchain is supplied as data on the SystemState, exactly as the real capture does, so these
/// run without WSL installed.
/// </summary>
public class LinuxFormatOperationTests
{
    private const ulong MB = 1024UL * 1024;
    private const ulong GB = 1024UL * MB;

    // ---------- fixtures ----------

    /// <summary>A toolchain where the named filesystems have a working mkfs and nothing else does.</summary>
    private static LinuxToolchainInfo Toolchain(params FileSystemType[] available)
    {
        var tools = new Dictionary<FileSystemType, LinuxToolAvailability>();
        foreach (var fs in new[]
                 {
                     FileSystemType.Ext4, FileSystemType.Ext3, FileSystemType.Ext2,
                     FileSystemType.Btrfs, FileSystemType.Xfs, FileSystemType.F2fs, FileSystemType.LinuxSwap
                 })
        {
            tools[fs] = available.Contains(fs)
                ? new LinuxToolAvailability(true, "Ubuntu", "/usr/sbin/" + fs.MkfsTool(), null)
                : new LinuxToolAvailability(false, null, null, $"{fs.MkfsTool()} not present in Ubuntu");
        }

        return new LinuxToolchainInfo
        {
            IsAvailable = available.Length > 0,
            BackendName = "WSL2 (Ubuntu)",
            SupportsDiskMount = true,
            Distros = new[] { new LinuxDistroInfo("Ubuntu", 2, true, true) },
            Tools = tools
        };
    }

    private static LinuxToolchainInfo NoWsl() => new()
    {
        IsAvailable = false,
        Reason = "WSL is not installed on this machine (wsl.exe was not found)."
    };

    private static PartitionInfo Partition(int number = 1, ulong size = 2 * GB, string? letter = "E")
        => new()
        {
            PartitionNumber = number,
            Kind = PartitionKind.Basic,
            OffsetBytes = MB,
            SizeBytes = size,
            DriveLetter = letter,
            Volume = new VolumeInfo
            {
                DriveLetter = letter, Label = "USB", FileSystem = "exFAT",
                SizeBytes = size, FreeBytes = size / 2, BitLocker = BitLockerInfo.NotEncryptable
            }
        };

    private static PhysicalDiskInfo Disk(
        ulong size = 4 * GB,
        PartitionStyle style = PartitionStyle.Gpt,
        bool removable = true,
        bool system = false,
        PartitionInfo[]? parts = null)
        => new()
        {
            Number = 3,
            FriendlyName = removable ? "SanDisk Ultra USB" : "Internal SSD",
            SizeBytes = size,
            Bus = removable ? StorageBus.Usb : StorageBus.Sata,
            Media = DiskMediaType.Ssd,
            IsRemovable = removable,
            IsSystemDisk = system,
            PartitionStyle = style,
            Capabilities = new DriveCapabilities { Supported = DriveCapability.Format | DriveCapability.PartitionEdit },
            Partitions = parts is { Length: > 0 } ? parts : new[] { Partition() }
        };

    /// <summary>A disk with one used partition and one unallocated gap, for create-partition tests.</summary>
    private static PhysicalDiskInfo DiskWithGap(out PartitionInfo gap)
    {
        var used = Partition(size: 1 * GB, letter: null);
        gap = new PartitionInfo { Kind = PartitionKind.Unallocated, OffsetBytes = 2 * GB, SizeBytes = 2 * GB };
        return Disk(parts: new[] { used, gap });
    }

    private static SystemState State(PhysicalDiskInfo disk, LinuxToolchainInfo linux, int? systemDisk = null)
        => new()
        {
            Disks = new[] { disk }, SystemDiskNumber = systemDisk, IsElevated = true, LinuxToolchain = linux
        };

    private static FormatVolumeSettings Reformat(
        FileSystemType fs = FileSystemType.Ext4, string label = "LINUXDATA", int part = 1, bool full = false)
        => new()
        {
            DiskNumber = 3, Scope = FormatScope.ReformatPartition, PartitionNumber = part,
            FileSystem = fs, Label = label, FullFormat = full
        };

    // ---------- the toolchain gate ----------

    [Fact]
    public void Ext4_IsValid_WhenMkfsIsAvailable()
    {
        var op = new FormatVolumeOperation(Reformat());
        var result = op.Validate(State(Disk(), Toolchain(FileSystemType.Ext4)));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
    }

    [Fact]
    public void Ext4_WorksWithNoWslAtAll()
    {
        // The whole point of the native writer: ext2/3/4 depend on nothing outside DiskForge, so a
        // machine with no WSL, no Hyper-V and no mkfs must still be able to format ext4.
        var result = new FormatVolumeOperation(Reformat()).Validate(State(Disk(), NoWsl()));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.DoesNotContain(result.Errors, e => e.Contains("WSL"));
    }

    [Theory]
    [InlineData(FileSystemType.Ext4)]
    [InlineData(FileSystemType.Ext3)]
    [InlineData(FileSystemType.Ext2)]
    public void EveryExtFilesystem_IsAvailableWithoutAToolchain(FileSystemType fs)
    {
        var result = new FormatVolumeOperation(Reformat(fs)).Validate(State(Disk(), NoWsl()));
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
    }

    [Fact]
    public void Btrfs_StillNeedsWsl_BecauseWeDoNotWriteItOurselves()
    {
        // Honesty in the other direction: we only claim what we can actually do.
        var result = new FormatVolumeOperation(Reformat(FileSystemType.Btrfs)).Validate(State(Disk(), NoWsl()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("WSL"));
    }

    [Fact]
    public void Btrfs_IsRefused_WhenOnlyExtToolsExist_AndNamesThePackage()
    {
        var op = new FormatVolumeOperation(Reformat(FileSystemType.Btrfs));
        var result = op.Validate(State(Disk(), Toolchain(FileSystemType.Ext4)));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e => e.Contains("mkfs.btrfs"));
        Assert.Contains("btrfs-progs", error);
    }

    [Fact]
    public void Ext4_IsUnaffectedByWslBeingUnableToAttachDisks()
    {
        // WSL cannot attach removable media at all. That used to sink ext4; it is now irrelevant,
        // because ext4 never goes near WSL.
        var toolchain = Toolchain(FileSystemType.Ext4);
        var noMount = new LinuxToolchainInfo
        {
            IsAvailable = true, BackendName = toolchain.BackendName, Tools = toolchain.Tools,
            Distros = toolchain.Distros, SupportsDiskMount = false
        };

        var result = new FormatVolumeOperation(Reformat()).Validate(State(Disk(), noMount));
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
    }

    [Fact]
    public void Btrfs_IsStillRefused_WhenWslCannotAttachDisks()
    {
        var toolchain = Toolchain(FileSystemType.Btrfs);
        var noMount = new LinuxToolchainInfo
        {
            IsAvailable = true, BackendName = toolchain.BackendName, Tools = toolchain.Tools,
            Distros = toolchain.Distros, SupportsDiskMount = false
        };

        var result = new FormatVolumeOperation(Reformat(FileSystemType.Btrfs)).Validate(State(Disk(), noMount));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("wsl --update"));
    }

    [Fact]
    public void WindowsFilesystems_AreUnaffectedByAMissingToolchain()
    {
        var op = new FormatVolumeOperation(Reformat(FileSystemType.Exfat, label: "USB"));
        var result = op.Validate(State(Disk(), NoWsl()));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
    }

    // ---------- the existing safety ladder still applies ----------

    [Fact]
    public void SystemDiskGuard_WinsOverLinuxFormats()
    {
        var disk = Disk(removable: false, system: true);
        var result = new FormatVolumeOperation(Reformat()).Validate(State(disk, Toolchain(FileSystemType.Ext4)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("system/boot disk"));
    }

    [Fact]
    public void InternalDiskGate_StillAppliesToLinuxFormats()
    {
        var disk = Disk(removable: false);
        var result = new FormatVolumeOperation(Reformat()).Validate(State(disk, Toolchain(FileSystemType.Ext4)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("INTERNAL"));
    }

    [Fact]
    public void EfiPartition_IsStillRefusedForLinuxFormats()
    {
        var efi = new PartitionInfo { PartitionNumber = 1, Kind = PartitionKind.Efi, SizeBytes = 100 * MB, IsSystem = true };
        var result = new FormatVolumeOperation(Reformat())
            .Validate(State(Disk(parts: new[] { efi }), Toolchain(FileSystemType.Ext4)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("system/EFI/recovery"));
    }

    [Fact]
    public void BitLockerVolume_IsStillRefusedForLinuxFormats()
    {
        var part = Partition() with
        {
            Volume = new VolumeInfo
            {
                FileSystem = "NTFS",
                BitLocker = new BitLockerInfo
                {
                    Protection = BitLockerProtection.On, Conversion = BitLockerConversion.FullyEncrypted
                }
            }
        };

        var result = new FormatVolumeOperation(Reformat())
            .Validate(State(Disk(parts: new[] { part }), Toolchain(FileSystemType.Ext4)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("BitLocker"));
    }

    // ---------- filesystem-specific limits ----------

    [Fact]
    public void Xfs_IsRefusedBelowItsMinimumSize()
    {
        var small = Partition(size: 200 * MB);
        var result = new FormatVolumeOperation(Reformat(FileSystemType.Xfs, label: "XFSVOL"))
            .Validate(State(Disk(size: 1 * GB, parts: new[] { small }), Toolchain(FileSystemType.Xfs)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("xfs needs at least"));
    }

    [Fact]
    public void Ext4_RejectsALabelOver16Bytes()
    {
        var result = new FormatVolumeOperation(Reformat(label: "THIS_LABEL_IS_MUCH_TOO_LONG"))
            .Validate(State(Disk(), Toolchain(FileSystemType.Ext4)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("16 bytes"));
    }

    [Fact]
    public void Ext4_CountsLabelLengthInUtf8Bytes_NotCharacters()
    {
        // 9 characters, but 18 bytes in UTF-8 — mke2fs measures bytes, so this must be refused.
        var result = new FormatVolumeOperation(Reformat(label: "ÆØÅÆØÅÆØÅ"))
            .Validate(State(Disk(), Toolchain(FileSystemType.Ext4)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("bytes"));
    }

    [Fact]
    public void Xfs_AllowsALabelThatWouldBeTooLongForItButFitsExt4()
    {
        // 14 chars: legal for ext4 (16) but over the XFS limit of 12.
        var forXfs = new FormatVolumeOperation(Reformat(FileSystemType.Xfs, label: "FOURTEEN_CHAR"))
            .Validate(State(Disk(size: 4 * GB), Toolchain(FileSystemType.Xfs)));
        Assert.False(forXfs.IsValid);

        var forExt4 = new FormatVolumeOperation(Reformat(FileSystemType.Ext4, label: "FOURTEEN_CHAR"))
            .Validate(State(Disk(), Toolchain(FileSystemType.Ext4)));
        Assert.True(forExt4.IsValid, string.Join(" ", forExt4.Errors));
    }

    [Fact]
    public void LinuxLabelsMayContainCharactersWindowsForbids()
    {
        // A colon is illegal in a Windows label but perfectly fine for ext4.
        Assert.Null(FormatVolumeOperation.LabelError("my:data", FileSystemType.Ext4));
        Assert.NotNull(FormatVolumeOperation.LabelError("my:data", FileSystemType.Ntfs));
    }

    [Fact]
    public void LinuxLabels_RejectControlCharactersAndSlash()
    {
        Assert.NotNull(FormatVolumeOperation.LabelError("bad\tlabel", FileSystemType.Ext4));
        Assert.NotNull(FormatVolumeOperation.LabelError("a/b", FileSystemType.Ext4));
    }

    // ---------- what the user is told ----------

    [Fact]
    public void Ext4_WarnsThatWindowsWillShowTheVolumeAsRaw()
    {
        var result = new FormatVolumeOperation(Reformat()).Validate(State(Disk(), Toolchain(FileSystemType.Ext4)));

        Assert.Contains(result.Warnings, w => w.Contains("RAW"));
        Assert.Contains(result.Warnings, w => w.Contains("mkfs.ext4"));
    }

    [Fact]
    public void Ext4_WarnsThatTheDriveLetterWillBeRemoved()
    {
        var result = new FormatVolumeOperation(Reformat()).Validate(State(Disk(), Toolchain(FileSystemType.Ext4)));

        Assert.Contains(result.Warnings, w => w.Contains("Drive letter E: will be removed"));
    }

    [Fact]
    public void FullFormat_OnXfs_IsHonestlyDowngraded()
    {
        var settings = Reformat(FileSystemType.Xfs, label: "XFSVOL", full: true);
        var op = new FormatVolumeOperation(settings);
        var result = op.Validate(State(Disk(), Toolchain(FileSystemType.Xfs)));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("no bad-block scan"));
        // …and the operation must not advertise itself as a full format.
        Assert.Contains("(quick)", op.Describe().Title);
    }

    [Fact]
    public void FullFormat_OnExt4_IsKept()
    {
        var op = new FormatVolumeOperation(Reformat(full: true));
        Assert.Contains("(full)", op.Describe().Title);
    }

    [Fact]
    public void Simulate_OnAFixedDisk_SpellsOutTheDirectWslHandoff()
    {
        var disk = Disk(removable: false);
        var settings = Reformat() with { AllowNonRemovable = true };
        var sim = new FormatVolumeOperation(settings).Simulate(State(disk, Toolchain(FileSystemType.Ext4)));

        Assert.True(sim.Feasible, sim.BlockingReason);
        Assert.Contains(sim.PlannedSteps, s => s.Contains("Remove drive letter"));
        Assert.Contains(sim.PlannedSteps, s => s.Contains("Linux filesystem data"));
        Assert.Contains(sim.PlannedSteps, s => s.Contains("wsl --mount --bare"));
        Assert.Contains(sim.PlannedSteps, s => s.Contains("mkfs.ext4"));
        Assert.Contains(sim.PlannedSteps, s => s.Contains("blkid"));
    }

    [Fact]
    public void Simulate_OnARemovableDisk_SpellsOutTheStagedImageRoute()
    {
        // Hyper-V refuses to attach removable media to the WSL VM, so the plan must describe the
        // scratch-image route rather than promising a direct handoff that cannot happen.
        var sim = new FormatVolumeOperation(Reformat()).Simulate(State(Disk(), Toolchain(FileSystemType.Ext4)));

        Assert.True(sim.Feasible, sim.BlockingReason);
        Assert.Contains(sim.PlannedSteps, s => s.Contains("scratch disk image"));
        Assert.Contains(sim.PlannedSteps, s => s.Contains("mkfs.ext4"));
        Assert.Contains(sim.PlannedSteps, s => s.Contains("superblock"));
        Assert.DoesNotContain(sim.PlannedSteps, s => s.Contains("wsl --mount --bare"));
    }

    // ---------- an offline disk is fixed, not refused ----------

    [Fact]
    public void AnOfflineDisk_IsAllowed_AndThePlanSaysItWillBeBroughtOnline()
    {
        // diskpart fails with "The operation is not allowed on a disk that is offline", and DiskForge
        // itself can leave a disk offline after a failed WSL attach — so this must self-heal rather
        // than trap the user.
        var disk = Disk();
        var offline = new PhysicalDiskInfo
        {
            Number = disk.Number, FriendlyName = disk.FriendlyName, SizeBytes = disk.SizeBytes,
            Bus = disk.Bus, Media = disk.Media, IsRemovable = true, IsOffline = true,
            PartitionStyle = disk.PartitionStyle, Capabilities = disk.Capabilities,
            Partitions = disk.Partitions
        };

        var op = new FormatVolumeOperation(Reformat());
        var state = State(offline, Toolchain(FileSystemType.Ext4));

        var validation = op.Validate(state);
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));
        Assert.Contains(validation.Warnings, w => w.Contains("offline") && w.Contains("brought"));

        Assert.Contains(op.Simulate(state).PlannedSteps, s => s.Contains("Bring disk 3 online"));
    }

    [Fact]
    public void AnOnlineDisk_IsNotToldAnythingAboutBeingBroughtOnline()
    {
        var state = State(Disk(), Toolchain(FileSystemType.Ext4));
        var op = new FormatVolumeOperation(Reformat());

        Assert.DoesNotContain(op.Validate(state).Warnings, w => w.Contains("offline"));
        Assert.DoesNotContain(op.Simulate(state).PlannedSteps, s => s.Contains("online"));
    }

    [Fact]
    public void RemovableDisks_AreNotWarnedAboutTheWholeDiskBeingTakenAway()
    {
        // That warning belongs to the direct route only — the staged route writes one partition.
        var second = Partition(number: 2, size: 1 * GB, letter: "G");
        var disk = Disk(parts: new[] { Partition(), second });
        var result = new FormatVolumeOperation(Reformat()).Validate(State(disk, Toolchain(FileSystemType.Ext4)));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("handed to WSL for the duration"));
        Assert.Contains(result.Warnings, w => w.Contains("scratch image"));
    }

    // ---------- generated commands ----------

    [Fact]
    public void ReformatPrep_DropsTheLetter_TagsThePartition_AndAssertsTheOffset()
    {
        var script = new FormatVolumeOperation(Reformat()).PreviewScript();

        Assert.Contains("Remove-PartitionAccessPath", script);
        Assert.Contains("Set-Partition", script);
        Assert.Contains("0fc63daf-8483-4772-8e79-3d69d8477de4", script);
        // Never Format-Volume: Windows cannot write ext4, and asking it to would fail loudly at Apply.
        Assert.DoesNotContain("Format-Volume", script);
        Assert.Contains("mkfs.ext4", script);
    }

    [Fact]
    public void CleanWholeDisk_ForLinux_MakesGptAndTagsIt_WithoutFormattingOrAssigning()
    {
        var settings = Reformat() with { Scope = FormatScope.CleanWholeDisk, PartitionNumber = null };
        var script = new FormatVolumeOperation(settings).PreviewScript();

        Assert.Contains("select disk 3", script);
        Assert.Contains("clean", script);
        Assert.Contains("Initialize-Disk -Number $n -PartitionStyle GPT", script);
        Assert.Contains("New-Partition -DiskNumber $n -UseMaximumSize", script);
        Assert.Contains("Set-Partition -GptType '{0fc63daf-8483-4772-8e79-3d69d8477de4}'", script);
        // diskpart cannot write ext4 and a drive letter would only invite Explorer to reformat it.
        Assert.DoesNotContain("format fs=", script);
        Assert.DoesNotContain("assign", script);
    }

    [Fact]
    public void CleanWholeDisk_ForLinux_DoesNotUseDiskpartConvert()
    {
        // `convert gpt` only accepts an EMPTY MBR disk. `clean` leaves removable media RAW, so the
        // convert fails with "The disk you specified is not MBR formatted" — the partitioning must be
        // done where the disk's actual state can be branched on.
        var settings = Reformat() with { Scope = FormatScope.CleanWholeDisk, PartitionNumber = null };
        var script = new FormatVolumeOperation(settings).PreviewScript();

        Assert.DoesNotContain("convert gpt", script);
        Assert.Contains("Initialize-Disk -Number $n -PartitionStyle GPT", script);
    }

    [Fact]
    public void CleanWholeDisk_ForLinux_ConvergesOnGpt_RatherThanAssumingAStartingState()
    {
        // Windows caches the disk layout and re-initializes a cleaned removable disk on its own, so
        // "already been initialized" is an expected outcome, not an error.
        var settings = Reformat() with { Scope = FormatScope.CleanWholeDisk, PartitionNumber = null };
        var script = new FormatVolumeOperation(settings).PreviewScript();

        Assert.Contains("Update-HostStorageCache", script);
        Assert.Contains("already been initialized", script);
    }

    [Fact]
    public void CleanWholeDisk_ForLinux_TagsThePartitionForEitherSchemeWindowsPicks()
    {
        // Windows repeatedly auto-initializes a cleaned removable disk as MBR. Rather than fight it,
        // the partition is tagged Linux under whichever scheme it lands on — GPT GUID or MBR 0x83.
        var settings = Reformat() with { Scope = FormatScope.CleanWholeDisk, PartitionNumber = null };
        var script = new FormatVolumeOperation(settings).PreviewScript();

        Assert.Contains("Set-Partition -GptType '{0fc63daf-8483-4772-8e79-3d69d8477de4}'", script);
        Assert.Contains("Set-Partition -MbrType 131", script);
    }

    [Fact]
    public void CleanWholeDisk_ForLinux_RetriesTheKnownPostCleanCapacityRace()
    {
        var settings = Reformat() with { Scope = FormatScope.CleanWholeDisk, PartitionNumber = null };
        var script = new FormatVolumeOperation(settings).PreviewScript();

        Assert.Contains("for ($i = 0; $i -lt 12 -and -not $p; $i++)", script);
        Assert.Contains("Could not create a partition on disk", script);
    }

    [Fact]
    public void MkfsArgv_ForcesTheWrite_PassesTheLabelAsItsOwnArgument_AndEndsWithTheDevice()
    {
        var argv = WslLinuxFormatBackend.BuildMkfsArgv(new LinuxFormatRequest
        {
            DiskNumber = 3, DiskSizeBytes = 4 * GB,
            PartitionOffsetBytes = MB, PartitionSizeBytes = 2 * GB,
            FileSystem = FileSystemType.Ext4, Label = "MY DATA"
        }, "/dev/sdc1");

        Assert.Equal(new[] { "mkfs.ext4", "-F", "-L", "MY DATA", "/dev/sdc1" }, argv);
    }

    [Fact]
    public void MkfsArgv_UsesEachToolsOwnFlags()
    {
        static IReadOnlyList<string> Argv(FileSystemType fs, bool scan = false) =>
            WslLinuxFormatBackend.BuildMkfsArgv(new LinuxFormatRequest
            {
                DiskNumber = 3, DiskSizeBytes = 4 * GB,
                PartitionOffsetBytes = MB, PartitionSizeBytes = 2 * GB,
                FileSystem = fs, Label = "DATA", BadBlockScan = scan
            }, "/dev/sdc1");

        Assert.Equal(new[] { "mkfs.xfs", "-f", "-L", "DATA", "/dev/sdc1" }, Argv(FileSystemType.Xfs));
        Assert.Equal(new[] { "mkfs.btrfs", "-f", "-L", "DATA", "/dev/sdc1" }, Argv(FileSystemType.Btrfs));
        // f2fs spells its label flag in lower case.
        Assert.Equal(new[] { "mkfs.f2fs", "-f", "-l", "DATA", "/dev/sdc1" }, Argv(FileSystemType.F2fs));
        Assert.Equal(new[] { "mkswap", "-f", "-L", "DATA", "/dev/sdc1" }, Argv(FileSystemType.LinuxSwap));

        // Bad-block scan only where the tool actually has one.
        Assert.Contains("-c", Argv(FileSystemType.Ext4, scan: true));
        Assert.DoesNotContain("-c", Argv(FileSystemType.Xfs, scan: true));
    }

    [Fact]
    public void MkfsArgv_OmitsTheLabelFlagWhenThereIsNoLabel()
    {
        var argv = WslLinuxFormatBackend.BuildMkfsArgv(new LinuxFormatRequest
        {
            DiskNumber = 3, DiskSizeBytes = 4 * GB,
            PartitionOffsetBytes = MB, PartitionSizeBytes = 2 * GB,
            FileSystem = FileSystemType.Ext4, Label = ""
        }, "/dev/sdc1");

        Assert.Equal(new[] { "mkfs.ext4", "-F", "/dev/sdc1" }, argv);
    }

    // ---------- create-partition, Linux variant ----------

    [Fact]
    public void CreatePartition_AsExt4_TagsTheTypeAndSkipsFormatVolume()
    {
        var op = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = 3, OffsetBytes = MB, SizeBytes = 1 * GB,
            FileSystem = FileSystemType.Ext4, Label = "LINUXDATA"
        });

        var script = op.PreviewScript();
        Assert.Contains("New-Partition", script);
        Assert.Contains("Set-Partition -GptType '{0fc63daf-8483-4772-8e79-3d69d8477de4}'", script);
        Assert.DoesNotContain("Format-Volume", script);
    }

    [Fact]
    public void CreatePartition_AsExt4_RefusesADriveLetter()
    {
        var disk = DiskWithGap(out _);
        var op = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = 3, OffsetBytes = 2 * GB, SizeBytes = 1 * GB,
            FileSystem = FileSystemType.Ext4, Label = "LINUXDATA", DriveLetter = "Z"
        });

        var result = op.Validate(State(disk, Toolchain(FileSystemType.Ext4)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("drive letter cannot be assigned"));
    }

    [Fact]
    public void CreatePartition_AsExt4_IsValidWithoutALetter_AndWarnsAboutRaw()
    {
        var disk = DiskWithGap(out _);
        var op = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = 3, OffsetBytes = 2 * GB, SizeBytes = 1 * GB,
            FileSystem = FileSystemType.Ext4, Label = "LINUXDATA"
        });

        var result = op.Validate(State(disk, Toolchain(FileSystemType.Ext4)));
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("RAW"));
    }

    [Fact]
    public void CreatePartition_AsBtrfs_IsRefusedWhenTheToolIsMissing()
    {
        var disk = DiskWithGap(out _);
        var op = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = 3, OffsetBytes = 2 * GB, SizeBytes = 1 * GB,
            FileSystem = FileSystemType.Btrfs, Label = "LINUXDATA"
        });

        var result = op.Validate(State(disk, Toolchain(FileSystemType.Ext4)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("mkfs.btrfs"));
    }
}
