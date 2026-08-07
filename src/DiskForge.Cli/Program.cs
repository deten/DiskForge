using DiskForge.Cli;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Default is the read-only dump. `provision` and `verify-media --write` are the destructive
// commands, and both demand --yes.
if (args.Length > 0 && string.Equals(args[0], "provision", StringComparison.OrdinalIgnoreCase))
{
    var code = await Provision.RunAsync(args);
    Log.CloseAndFlush();
    return code;
}

if (args.Length > 0 && string.Equals(args[0], "verify-media", StringComparison.OrdinalIgnoreCase))
{
    var code = await MediaVerify.RunAsync(args);
    Log.CloseAndFlush();
    return code;
}

var state = new SystemInspector().Capture();

Console.WriteLine();
Console.WriteLine("===============================================================");
Console.WriteLine($" DiskForge — read-only disk inspection   {state.CapturedAt:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine($" Elevated: {(state.IsElevated ? "yes" : "NO (writes would be blocked)")}   " +
                  $"System disk: {(state.SystemDiskNumber?.ToString() ?? "unknown")}");
Console.WriteLine("===============================================================");

foreach (var disk in state.Disks)
{
    Console.WriteLine();
    var flags = new List<string>();
    if (disk.IsSystemDisk) flags.Add("SYSTEM");
    if (disk.IsBootDisk) flags.Add("BOOT");
    if (disk.IsReadOnly) flags.Add("READONLY");
    if (disk.IsOffline) flags.Add("OFFLINE");
    if (disk.IsRemovable) flags.Add("REMOVABLE");
    var flagStr = flags.Count > 0 ? "  [" + string.Join(",", flags) + "]" : "";

    Console.WriteLine($"Disk {disk.Number}: {disk.FriendlyName}{flagStr}");
    Console.WriteLine($"  {Size(disk.SizeBytes)}  |  bus={disk.Bus}  media={disk.Media}  " +
                      $"scheme={disk.PartitionStyle}  health={disk.Health}");
    Console.WriteLine($"  model={disk.Model ?? "?"}  fw={disk.FirmwareVersion ?? "?"}  sn={disk.SerialNumber ?? "?"}");
    Console.WriteLine($"  sector logical/physical={disk.LogicalSectorSize?.ToString() ?? "?"}/" +
                      $"{disk.PhysicalSectorSize?.ToString() ?? "?"}  solidState={disk.IsSolidState}");

    if (disk.Link is { } link)
    {
        Console.WriteLine($"  Connection: {link.Interface}" +
                          (link.NegotiatedSpeed is { } n ? $"  negotiated={n}" : "") +
                          (link.CapableSpeed is { } c ? $"  capable={c}" : ""));
        if (link.IsUnderNegotiated)
            Console.WriteLine($"    ⚠ {link.MismatchHint}");
        if (link.FormFactor is { } ff)
            Console.WriteLine($"    form factor: {ff}");
        foreach (var note in link.Notes)
            Console.WriteLine($"    note: {note}");
    }

    Console.WriteLine($"  SED: {disk.Sed.Type}/{disk.Sed.Lock}   " +
                      $"encryptedVolume={disk.HasEncryptedVolume}");
    PrintCapabilities(disk.Capabilities);

    Console.WriteLine("  Partition map:");
    foreach (var p in disk.Partitions)
    {
        if (p.IsUnallocated)
        {
            Console.WriteLine($"    · [unallocated]           {Size(p.SizeBytes),10}  @ {Size(p.OffsetBytes)}");
            continue;
        }
        var vol = p.Volume;
        var label = vol?.Label is { Length: > 0 } ? $"\"{vol.Label}\" " : "";
        var fs = vol?.FileSystem ?? "-";
        // A Linux volume is identified from its superblock, not mounted, so there is no free-space
    // figure — printing "200 MB/200 MB used" would invent a measurement.
    var used = vol switch
    {
        { UsageKnown: true } => $"{Size(vol.UsedBytes)}/{Size(vol.SizeBytes)} used",
        not null => "usage not readable by Windows",
        _ => ""
    };
        var bl = vol?.BitLocker is { } b && b.Protection != BitLockerProtection.NotEncryptable
            ? $"  BitLocker:{b.Protection}" + (b.IsConverting ? $"({b.ConversionPercent}%)" : "")
            : "";
        var let = p.DriveLetter is { } dl ? $"{dl}: " : "   ";
        Console.WriteLine($"    #{p.PartitionNumber} {let}{p.Kind,-10} {label}{fs,-6} " +
                          $"{Size(p.SizeBytes),10}  {used}{bl}");
        if (vol?.FileSystemUuid is { Length: > 0 } uuid)
            Console.WriteLine($"         UUID {uuid}");
    }
}

PrintLinuxToolchain(state.LinuxToolchain);

Console.WriteLine();
Log.CloseAndFlush();
return 0;

static void PrintLinuxToolchain(LinuxToolchainInfo linux)
{
    Console.WriteLine();
    Console.WriteLine("Linux filesystem support");
    if (!linux.IsAvailable)
    {
        Console.WriteLine($"  unavailable: {linux.Reason}");
        return;
    }

    Console.WriteLine($"  backend: {linux.BackendName}" +
                      (linux.VersionText is { Length: > 0 } v ? $"  ({v})" : ""));
    Console.WriteLine($"  distros: {string.Join(", ", linux.Distros.Select(d => $"{d.Name} [wsl{d.WslVersion}{(d.IsDefault ? ", default" : "")}]"))}");
    Console.WriteLine($"  disk passthrough (wsl --mount): {(linux.SupportsDiskMount ? "yes" : "no")}");

    foreach (var (fs, tool) in linux.Tools.OrderBy(kv => kv.Key.ToFormatName(), StringComparer.Ordinal))
    {
        Console.WriteLine(tool.Available
            ? $"    {fs.ToFormatName(),-6} ✓  {tool.Path} ({tool.Distro})"
            : $"    {fs.ToFormatName(),-6} ✗  {tool.Reason}");
    }
}

static void PrintCapabilities(DriveCapabilities caps)
{
    var supported = Enum.GetValues<DriveCapability>()
        .Where(c => c != DriveCapability.None && caps.Has(c))
        .Select(c => c.ToString());
    Console.WriteLine($"  Capabilities: {string.Join(", ", supported)}");
    Console.WriteLine($"    freeze={caps.AtaSecurityFreeze}  smart={caps.SmartAvailable}");

    var notable = new[]
    {
        DriveCapability.AtaSecureErase, DriveCapability.NvmeSanitize,
        DriveCapability.TcgCryptoErase, DriveCapability.Smart
    };
    foreach (var cap in notable)
    {
        var reason = caps.ReasonUnavailable(cap);
        if (reason is not null)
            Console.WriteLine($"    unavailable {cap}: {reason}");
    }
}

static string Size(ulong bytes)
{
    string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
    double v = bytes;
    int i = 0;
    while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
    return $"{v:0.##} {units[i]}";
}
