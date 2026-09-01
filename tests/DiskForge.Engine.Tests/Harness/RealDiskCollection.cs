namespace DiskForge.Engine.Tests.Harness;

/// <summary>
/// Every test that attaches a VHDX or writes to a real disk belongs here.
///
/// xUnit runs test classes in parallel by default, and these classes do not have independent state to
/// be parallel over: they attach and detach physical disks, run diskpart, and hand out drive letters,
/// all of which are machine-wide. Run concurrently they interfere in ways that look like product bugs.
/// A clone test that passed on its own failed inside the full suite because another class was churning
/// disks at the same moment, and chasing that as a real defect would have been wasted effort.
///
/// Marking the classes with this collection serialises them against each other while leaving the
/// several hundred pure-logic tests running in parallel, so the suite stays quick.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealDiskCollection
{
    public const string Name = "real-disk";
}
