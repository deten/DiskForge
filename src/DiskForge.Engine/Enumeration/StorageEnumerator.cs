using System.Management;
using DiskForge.Core.Model;
using DiskForge.Engine.Capabilities;
using DiskForge.Engine.Linux;
using DiskForge.Engine.Native;
using Serilog;

namespace DiskForge.Engine.Enumeration;

/// <summary>
/// Reads the real storage topology from the Windows Storage Management WMI provider
/// (root\Microsoft\Windows\Storage) plus BitLocker state, and assembles the immutable
/// <see cref="PhysicalDiskInfo"/> graph. Strictly read-only (Phase 2).
/// </summary>
public sealed class StorageEnumerator
{
    private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";

    private sealed record VolumeRecord(string? Path, string? Letter, string Fs, string? Label, ulong Size, ulong Free);

    public IReadOnlyList<PhysicalDiskInfo> EnumerateDisks()
    {
        var scope = new ManagementScope(StorageScope);
        scope.Connect();

        var media = ReadPhysicalMedia(scope);
        var volumes = ReadVolumes(scope);
        var partitionsByDisk = ReadPartitions(scope, volumes);
        var bitlocker = BitLockerProbe.ProbeByDriveLetter();

        var disks = new List<PhysicalDiskInfo>();
        foreach (var disk in Query(scope, "SELECT * FROM MSFT_Disk"))
        {
            using (disk)
            {
                var number = (int)disk.GetU32("Number");
                var bus = (StorageBus)disk.GetU16("BusType");
                var serial = disk.GetString("SerialNumber");
                var (mediaType, health) = ResolveMedia(media, serial, number);
                var isRemovable = bus is StorageBus.Usb or StorageBus.Sd or StorageBus.Mmc;

                var probe = PhysicalDriveProbe.Probe(number);
                var caps = CapabilityProfiler.Profile(bus, mediaType, isRemovable, probe);
                var solidState = mediaType is DiskMediaType.Ssd or DiskMediaType.Scm || bus == StorageBus.Nvme;
                var link = LinkProbe.Probe(number, bus, mediaType, solidState);

                var style = (PartitionStyle)disk.GetU16("PartitionStyle");
                var reals = partitionsByDisk.TryGetValue(number, out var list) ? list : new List<PartitionInfo>();
                reals = AttachBitLocker(reals, bitlocker);
                reals = AttachLinuxFilesystems(number, reals);
                var map = DiskMap.Build(reals, disk.GetU64("Size"), style);

                disks.Add(new PhysicalDiskInfo
                {
                    Number = number,
                    FriendlyName = disk.GetString("FriendlyName") ?? $"Disk {number}",
                    Model = disk.GetString("Model"),
                    SerialNumber = serial,
                    FirmwareVersion = disk.GetString("FirmwareVersion"),
                    SizeBytes = disk.GetU64("Size"),
                    Bus = bus,
                    Media = mediaType,
                    Health = health,
                    PartitionStyle = style,
                    LogicalSectorSize = disk.GetU32Nullable("LogicalSectorSize"),
                    PhysicalSectorSize = disk.GetU32Nullable("PhysicalSectorSize"),
                    IsBootDisk = disk.GetBool("IsBoot"),
                    IsSystemDisk = disk.GetBool("IsSystem"),
                    IsReadOnly = disk.GetBool("IsReadOnly"),
                    IsOffline = disk.GetBool("IsOffline"),
                    IsRemovable = isRemovable,
                    Sed = SedInfo.NotDetected with { Type = SedType.Unknown, Lock = SedLockState.Unknown },
                    Capabilities = caps,
                    Link = link,
                    Partitions = map
                });
            }
        }

        return disks.OrderBy(d => d.Number).ToList();
    }

    /// <summary>
    /// Fills in the filesystem type and label for Linux partitions by reading their superblock off the
    /// platter. Windows has no ext4/btrfs/xfs driver, so it cannot name these — the bytes are on the
    /// disk, only Windows can't read them.
    ///
    /// Windows may or may not build a volume object over such a partition, and both cases must be
    /// handled: with no volume it shows as a nameless "Linux" block, and *with* one it shows as "RAW"
    /// with a phantom "0 B of 0 B used". Keying only on "no volume" missed the second case entirely,
    /// which is what left six freshly-labelled partitions on a real stick showing as unnamed RAW.
    ///
    /// Best-effort by design: the raw read needs Administrator, and unelevated (or on a disk that is
    /// busy) it simply yields nothing rather than failing enumeration.
    /// </summary>
    private static List<PartitionInfo> AttachLinuxFilesystems(int diskNumber, List<PartitionInfo> parts)
    {
        if (!parts.Any(NeedsSuperblockRead)) return parts;

        return parts.Select(p =>
        {
            if (!NeedsSuperblockRead(p)) return p;
            try
            {
                if (LinuxFsSignature.Read(diskNumber, p.OffsetBytes) is not { } fs) return p;
                return p with
                {
                    Volume = new VolumeInfo
                    {
                        Label = fs.Label,
                        FileSystem = fs.Type,
                        SizeBytes = p.SizeBytes,
                        FreeBytes = 0,
                        UsageKnown = false,       // no free-space figure without mounting it
                        MountedByWindows = false, // so nothing offers a Windows-side rename/letter
                        // Keep whatever access path Windows built. It is what dismounts the volume
                        // before a raw write, so the filesystem's own UUID must not displace it.
                        DriveLetter = p.Volume?.DriveLetter,
                        UniqueId = p.Volume?.UniqueId,
                        FileSystemUuid = fs.Uuid
                    }
                };
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read a Linux superblock on disk {Disk} at {Offset}",
                    diskNumber, p.OffsetBytes);
                return p;
            }
        }).ToList();
    }

    /// <summary>
    /// A Linux-typed partition that Windows could not identify — either no volume at all, or a volume
    /// it had to call RAW. Anything Windows *did* identify is left alone.
    /// </summary>
    private static bool NeedsSuperblockRead(PartitionInfo p)
    {
        if (p.Kind != PartitionKind.Linux) return false;
        if (p.Volume is not { } vol) return true;

        return string.IsNullOrWhiteSpace(vol.FileSystem)
               || vol.FileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase)
               || vol.FileSystem.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static List<PartitionInfo> AttachBitLocker(
        List<PartitionInfo> parts, IReadOnlyDictionary<string, BitLockerInfo> bitlocker)
    {
        return parts.Select(p =>
        {
            if (p.Volume is null || p.DriveLetter is null) return p;
            if (!bitlocker.TryGetValue(p.DriveLetter, out var bl)) return p;
            var vol = p.Volume with { BitLocker = bl };
            return p with { Volume = vol };
        }).ToList();
    }

    private (DiskMediaType, HealthStatus) ResolveMedia(
        IReadOnlyDictionary<string, (DiskMediaType, HealthStatus)> media, string? serial, int number)
    {
        if (serial is not null && media.TryGetValue("sn:" + serial, out var bySerial)) return bySerial;
        if (media.TryGetValue("id:" + number, out var byId)) return byId;
        return (DiskMediaType.Unknown, HealthStatus.Unknown);
    }

    private IReadOnlyDictionary<string, (DiskMediaType, HealthStatus)> ReadPhysicalMedia(ManagementScope scope)
    {
        var map = new Dictionary<string, (DiskMediaType, HealthStatus)>();
        foreach (var pd in Query(scope, "SELECT * FROM MSFT_PhysicalDisk"))
        {
            using (pd)
            {
                var media = (DiskMediaType)pd.GetU16("MediaType");
                var health = pd.GetU16("HealthStatus") switch
                {
                    0 => HealthStatus.Healthy,
                    1 => HealthStatus.Warning,
                    2 => HealthStatus.Unhealthy,
                    _ => HealthStatus.Unknown
                };
                var value = (media, health);
                if (pd.GetString("SerialNumber") is { } sn) map["sn:" + sn] = value;
                if (pd.GetString("DeviceId") is { } id) map["id:" + id] = value;
            }
        }
        return map;
    }

    private IReadOnlyList<VolumeRecord> ReadVolumes(ManagementScope scope)
    {
        var vols = new List<VolumeRecord>();
        foreach (var v in Query(scope, "SELECT * FROM MSFT_Volume"))
        {
            using (v)
            {
                vols.Add(new VolumeRecord(
                    Path: v.GetString("Path"),
                    Letter: v.GetDriveLetter("DriveLetter"),
                    Fs: v.GetString("FileSystem") ?? "RAW",
                    Label: v.GetString("FileSystemLabel"),
                    Size: v.GetU64("Size"),
                    Free: v.GetU64("SizeRemaining")));
            }
        }
        return vols;
    }

    private IReadOnlyDictionary<int, List<PartitionInfo>> ReadPartitions(
        ManagementScope scope, IReadOnlyList<VolumeRecord> volumes)
    {
        var byDisk = new Dictionary<int, List<PartitionInfo>>();
        foreach (var part in Query(scope, "SELECT * FROM MSFT_Partition"))
        {
            using (part)
            {
                var diskNumber = (int)part.GetU32("DiskNumber");
                var letter = part.GetDriveLetter("DriveLetter");
                var accessPaths = part.GetStringArray("AccessPaths");
                var gpt = PartitionTypes.ParseGuid(part.GetString("GptType"));
                var isSystem = part.GetBool("IsSystem");

                var vol = MatchVolume(volumes, accessPaths, letter);
                var info = new PartitionInfo
                {
                    PartitionNumber = (int)part.GetU32("PartitionNumber"),
                    OffsetBytes = part.GetU64("Offset"),
                    SizeBytes = part.GetU64("Size"),
                    Kind = PartitionTypes.Classify(gpt, part.GetString("Type"), isSystem,
                        gpt is null ? (byte?)part.GetU16("MbrType") : null),
                    GptType = gpt,
                    MbrType = gpt is null ? (byte?)part.GetU16("MbrType") : null,
                    DriveLetter = letter ?? vol?.Letter,
                    IsBoot = part.GetBool("IsBoot"),
                    IsSystem = isSystem,
                    IsActive = part.GetBool("IsActive"),
                    IsHidden = part.GetBool("IsHidden"),
                    Volume = vol is null ? null : new VolumeInfo
                    {
                        DriveLetter = vol.Letter,
                        Label = vol.Label,
                        FileSystem = vol.Fs,
                        SizeBytes = vol.Size,
                        FreeBytes = vol.Free,
                        UniqueId = vol.Path
                    }
                };

                if (!byDisk.TryGetValue(diskNumber, out var list))
                    byDisk[diskNumber] = list = new List<PartitionInfo>();
                list.Add(info);
            }
        }
        return byDisk;
    }

    private static VolumeRecord? MatchVolume(IReadOnlyList<VolumeRecord> volumes, string[] accessPaths, string? letter)
    {
        foreach (var ap in accessPaths)
        {
            var trimmed = ap.TrimEnd('\\');
            var hit = volumes.FirstOrDefault(v => v.Path is { } p &&
                string.Equals(p.TrimEnd('\\'), trimmed, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        if (letter is not null)
            return volumes.FirstOrDefault(v => string.Equals(v.Letter, letter, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private static IEnumerable<ManagementObject> Query(ManagementScope scope, string wql)
    {
        ManagementObjectCollection results;
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
            results = searcher.Get();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WMI query failed: {Query}", wql);
            yield break;
        }
        foreach (ManagementObject o in results.Cast<ManagementObject>())
            yield return o;
    }
}
