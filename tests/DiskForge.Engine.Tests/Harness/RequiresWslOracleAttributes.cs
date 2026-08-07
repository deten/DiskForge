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
            var hasDistro = LinuxToolchainProbe.Get().Distros.Any(d => d.WslVersion == 2);
            return hasDistro
                ? null
                : "Needs a WSL2 distro with e2fsprogs to run e2fsck as a correctness oracle. " +
                  "The product itself does not need WSL — only this check does.";
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
