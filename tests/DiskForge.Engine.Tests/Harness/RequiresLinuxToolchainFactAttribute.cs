using DiskForge.Core.Operations;
using DiskForge.Engine;
using DiskForge.Engine.Linux;

namespace DiskForge.Engine.Tests.Harness;

/// <summary>
/// A <see cref="FactAttribute"/> for tests that write a real Linux filesystem: they need Administrator
/// (VHDX attach + <c>wsl --mount</c>) <b>and</b> a WSL distribution that actually has the mkfs tool.
/// Missing either is a skip with the specific reason, never a silent pass and never a failure — a
/// machine without btrfs-progs is not a broken build.
/// </summary>
public sealed class RequiresLinuxToolchainFactAttribute : FactAttribute
{
    public RequiresLinuxToolchainFactAttribute(
        FileSystemType fileSystem = FileSystemType.Ext4, bool verifiesWithBlkid = true)
    {
        if (!Elevation.IsElevated())
        {
            Skip = "Requires Administrator (VHDX attach + wsl --mount). Run 'dotnet test' from an elevated shell.";
            return;
        }

        var toolchain = LinuxToolchainProbe.Get();

        var blocked = toolchain.BlockingReason(fileSystem);
        if (blocked is not null)
        {
            Skip = $"Requires a WSL distro that can write {fileSystem.ToFormatName()}: {blocked}";
            return;
        }

        // These tests judge the result with blkid, deliberately, so that our own reader is never the
        // thing agreeing with our own writer. That makes blkid a hard requirement even for ext, which
        // DiskForge now writes natively: BlockingReason returns null for ext because nothing about the
        // host can stop the *write*, but the *verification* still goes through WSL. Without this the
        // ext cases fail on a machine whose WSL will not start, which is an environment problem being
        // reported as a broken build. Pass verifiesWithBlkid: false for a test that writes ext natively
        // and checks the result through DiskForge's own read-back, which needs no WSL at all.
        if (verifiesWithBlkid && !toolchain.SupportToolPaths.ContainsKey("blkid"))
            Skip = "Requires blkid in a WSL distro: these tests use it as the independent judge of what " +
                   "was written (install util-linux inside your distro, or repair WSL).";
    }
}
