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
    public RequiresLinuxToolchainFactAttribute(FileSystemType fileSystem = FileSystemType.Ext4)
    {
        if (!Elevation.IsElevated())
        {
            Skip = "Requires Administrator (VHDX attach + wsl --mount). Run 'dotnet test' from an elevated shell.";
            return;
        }

        var blocked = LinuxToolchainProbe.Get().BlockingReason(fileSystem);
        if (blocked is not null)
            Skip = $"Requires a WSL distro that can write {fileSystem.ToFormatName()}: {blocked}";
    }
}
