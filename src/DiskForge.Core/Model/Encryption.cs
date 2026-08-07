namespace DiskForge.Core.Model;

/// <summary>Hardware self-encryption state of a physical disk (§1A.2).</summary>
public sealed record SedInfo
{
    public static readonly SedInfo NotDetected = new() { Type = SedType.None, Lock = SedLockState.NotApplicable };

    public SedType Type { get; init; } = SedType.Unknown;
    public SedLockState Lock { get; init; } = SedLockState.Unknown;

    /// <summary>PSID printed on the drive label — the crypto-revert recovery escape hatch.</summary>
    public bool PsidRevertSupported { get; init; }

    public bool IsSelfEncrypting => Type is not (SedType.None or SedType.Unknown);
}

/// <summary>BitLocker / software-encryption state of a volume (§1A.3). Bus-independent.</summary>
public sealed class BitLockerInfo
{
    public static readonly BitLockerInfo NotEncryptable = new()
    {
        Protection = BitLockerProtection.NotEncryptable,
        Conversion = BitLockerConversion.Unknown
    };

    public BitLockerProtection Protection { get; init; } = BitLockerProtection.Unknown;
    public BitLockerConversion Conversion { get; init; } = BitLockerConversion.Unknown;

    /// <summary>Encryption/decryption progress percentage when a conversion is in flight.</summary>
    public int? ConversionPercent { get; init; }

    public string? EncryptionMethod { get; init; }

    /// <summary>Key-protector types present (TPM, TpmPin, RecoveryPassword, StartupKey, ...).</summary>
    public IReadOnlyList<string> KeyProtectors { get; init; } = Array.Empty<string>();

    public bool IsProtected => Protection == BitLockerProtection.On;

    /// <summary>True while encrypting or decrypting — a hard gate for clone/resize/move (§1A.3).</summary>
    public bool IsConverting =>
        Conversion is BitLockerConversion.EncryptionInProgress or BitLockerConversion.DecryptionInProgress;
}
