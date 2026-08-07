namespace DiskForge.Core.Model;

/// <summary>
/// Discrete hardware/firmware capabilities a physical drive may or may not support.
/// Populated per drive by the capability probe (§1A) and compared against an
/// operation's <c>RequiredCapabilities()</c> mask to gate execution (§1A.7).
/// </summary>
[Flags]
public enum DriveCapability : ulong
{
    None = 0,

    // Observation / health
    Smart = 1UL << 0,
    Temperature = 1UL << 1,

    // Self-encrypting-drive families
    TcgOpal = 1UL << 2,
    AtaSecurity = 1UL << 3,
    Edrive = 1UL << 4,

    // Secure-erase mechanisms (capability-routed wipe, §1A.5)
    AtaSecureErase = 1UL << 5,
    AtaSanitize = 1UL << 6,
    NvmeFormat = 1UL << 7,
    NvmeSanitize = 1UL << 8,
    ScsiSanitize = 1UL << 9,
    TcgCryptoErase = 1UL << 10,

    // Data-management
    Trim = 1UL << 11,
    HpaDco = 1UL << 12,

    // Generic block-device operations (always available regardless of bus)
    PartitionEdit = 1UL << 13,
    Format = 1UL << 14,
    Clone = 1UL << 15,
    Image = 1UL << 16,

    // Overwrite fallback (HDD / hardware-erase-unsupported)
    SectorOverwrite = 1UL << 17
}
