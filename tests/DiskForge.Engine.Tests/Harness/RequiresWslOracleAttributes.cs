using DiskForge.Engine.Linux;

namespace DiskForge.Engine.Tests.Harness;

/// <summary>
/// Marks a test that uses e2fsprogs inside WSL as an <b>oracle</b> — to check that an image DiskForge
/// wrote is a valid filesystem according to Linux itself.
///
/// This is the one place WSL is legitimately involved: the shipping product formats drives with no
/// external tool, but proving a hand-written filesystem is correct requires an independent judge, and
/// e2fsck is that judge. No elevation is needed because the oracle only ever reads a <i>file</i>.
/// Tests skip (never fail) when no WSL2 distro is present.
/// </summary>
internal static class WslOracle
{
    /// <summary>Reason the oracle is unusable, or null when it is available.</summary>
    public static string? Unavailable()
    {
        try
        {
            // "A distro is listed" is not the same as "the oracle answers". On 2026-09-01 this machine's
            // WSL listed two distros and could not start either (HCS/ERROR_FILE_NOT_FOUND), and every
            // oracle test failed on the e2fsck call one step past the old gate. The probe only records a
            // tool path after it has actually run a command inside the distro, so seeing e2fsck proves
            // both that the VM starts and that e2fsprogs is installed.
            var toolchain = LinuxToolchainProbe.Get();
            if (!toolchain.Distros.Any(d => d.WslVersion == 2))
                return "Needs a WSL2 distro with e2fsprogs to run e2fsck as a correctness oracle. " +
                       "The product itself does not need WSL, only this check does.";
            if (!toolchain.SupportToolPaths.ContainsKey("e2fsck"))
                return "A WSL2 distro is installed but e2fsck could not be reached inside it (WSL will not " +
                       "start, or e2fsprogs is not installed). The product itself does not need WSL, only " +
                       "this check does.";
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not probe for a WSL2 oracle: {ex.Message}";
        }
    }
}

public sealed class RequiresWslOracleFactAttribute : FactAttribute
{
    public RequiresWslOracleFactAttribute()
    {
        if (WslOracle.Unavailable() is { } reason) Skip = reason;
    }
}

public sealed class RequiresWslOracleTheoryAttribute : TheoryAttribute
{
    public RequiresWslOracleTheoryAttribute()
    {
        if (WslOracle.Unavailable() is { } reason) Skip = reason;
    }
}
