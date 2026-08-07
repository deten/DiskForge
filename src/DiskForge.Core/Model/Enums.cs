namespace DiskForge.Core.Model;

/// <summary>Physical connection/transport of a drive. Mirrors MSFT_PhysicalDisk BusType.</summary>
public enum StorageBus
{
    Unknown = 0,
    Scsi = 1,
    Atapi = 2,
    Ata = 3,
    Ieee1394 = 4,
    Ssa = 5,
    FibreChannel = 6,
    Usb = 7,
    Raid = 8,
    Iscsi = 9,
    Sas = 10,
    Sata = 11,
    Sd = 12,
    Mmc = 13,
    FileBackedVirtual = 15,
    StorageSpaces = 16,
    Nvme = 17,
    Scm = 18,
    Ufs = 19
}

/// <summary>Physical media class. Mirrors MSFT_PhysicalDisk MediaType.</summary>
public enum DiskMediaType
{
    Unknown = 0,
    Hdd = 3,
    Ssd = 4,
    Scm = 5
}

/// <summary>On-disk partitioning scheme.</summary>
public enum PartitionStyle
{
    Unknown = 0,
    Mbr = 1,
    Gpt = 2,
    Raw = 3
}

public enum HealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Warning = 2,
    Unhealthy = 3
}

/// <summary>Self-encrypting-drive class detected on a physical disk.</summary>
public enum SedType
{
    Unknown = 0,
    None = 1,
    TcgOpal = 2,
    TcgEnterprise = 3,
    TcgPyrite = 4,
    TcgRuby = 5,
    TcgOpalite = 6,
    AtaSecurity = 7,
    Edrive = 8
}

public enum SedLockState
{
    Unknown = 0,
    NotApplicable = 1,
    LockingDisabled = 2,
    Unlocked = 3,
    Locked = 4
}

/// <summary>ATA security freeze state — must be cleared (power cycle) before Secure Erase.</summary>
public enum FreezeState
{
    Unknown = 0,
    NotApplicable = 1,
    NotFrozen = 2,
    Frozen = 3
}

public enum BitLockerProtection
{
    Unknown = 0,
    NotEncryptable = 1,
    Off = 2,
    On = 3,
    Suspended = 4
}

public enum BitLockerConversion
{
    Unknown = 0,
    FullyDecrypted = 1,
    FullyEncrypted = 2,
    EncryptionInProgress = 3,
    DecryptionInProgress = 4
}
