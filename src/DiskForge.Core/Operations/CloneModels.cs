namespace DiskForge.Core.Operations;

/// <summary>How the clone copies data from source to target.</summary>
public enum CloneMethod
{
    /// <summary>Byte-for-byte copy of the whole disk (every sector). Target must be ≥ source size.
    /// Faithful and filesystem-agnostic, but copies unused space too.</summary>
    FullSector = 0,

    /// <summary>Copy only the partitioned region up to the end of the last partition, then let the
    /// clone's GPT be repaired to span the target. Skips the trailing unallocated tail of the source.
    /// Still target ≥ used-extent; NOT a shrinking clone.</summary>
    UsedExtent = 1
}

/// <summary>What DiskForge will do to make a cloned OS disk bootable. Recorded on the plan so the UI
/// and logs are explicit about the boot outcome (§ "boots cleanly").</summary>
public enum BootHandling
{
    /// <summary>Source carries no OS/boot role — nothing to do; it's a data clone.</summary>
    NotBootable = 0,

    /// <summary>Source is self-contained bootable (its own ESP + OS): after copy we regenerate disk
    /// identity and rebuild the boot files on the clone's ESP so it boots on its own.</summary>
    RebuildBootFiles = 1,

    /// <summary>Source hosts the OS but its ESP lives on a DIFFERENT physical disk (split-boot). A clone
    /// of this disk alone cannot boot; the user must also clone the disk holding the ESP.</summary>
    EspOnAnotherDisk = 2
}

/// <summary>Parameters for a whole-disk clone. The target is fully overwritten (DESTRUCTIVE).</summary>
public sealed record CloneDiskSettings
{
    public required int SourceDiskNumber { get; init; }
    public required int TargetDiskNumber { get; init; }

    public CloneMethod Method { get; init; } = CloneMethod.FullSector;

    /// <summary>Regenerate GPT disk + partition GUIDs on the clone so it doesn't collide with the source
    /// when both are attached. Almost always wanted; off only for a true forensic duplicate.</summary>
    public bool RegenerateDiskIdentity { get; init; } = true;

    /// <summary>Attempt to rebuild boot files (bcdboot) when the source is self-contained bootable.</summary>
    public bool MakeBootable { get; init; } = true;

    /// <summary>Read every copied byte back and compare hashes after writing (the verify pass).</summary>
    public bool VerifyAfter { get; init; } = true;

    /// <summary>Required to target an INTERNAL (non-removable) disk, mirroring the other write ops.</summary>
    public bool AllowNonRemovableTarget { get; init; }

    /// <summary>Acknowledge that the source has in-use/mounted volumes and the copy will be only
    /// crash-consistent (no VSS snapshot yet — live snapshotting is Coming Soon).</summary>
    public bool AllowLiveCrashConsistent { get; init; }
}
