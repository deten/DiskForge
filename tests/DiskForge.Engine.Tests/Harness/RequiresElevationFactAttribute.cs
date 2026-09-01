using DiskForge.Engine;

namespace DiskForge.Engine.Tests.Harness;

/// <summary>
/// A <see cref="FactAttribute"/> that auto-skips when the test host is not elevated. VHDX attach and
/// all real write operations need Administrator, so these tests run only from an elevated shell and
/// are skipped (not failed) otherwise — keeping unelevated/CI runs green.
/// </summary>
public sealed class RequiresElevationFactAttribute : FactAttribute
{
    public RequiresElevationFactAttribute()
    {
        if (!Elevation.IsElevated())
            Skip = "Requires Administrator (VHDX attach). Run: right-click PowerShell > Run as administrator, then 'dotnet test'.";
    }
}

/// <summary>
/// The <see cref="TheoryAttribute"/> twin of <see cref="RequiresElevationFactAttribute"/>, so a matrix
/// of elevated cases skips as one rather than needing a fact per row.
/// </summary>
public sealed class RequiresElevationTheoryAttribute : TheoryAttribute
{
    public RequiresElevationTheoryAttribute()
    {
        if (!Elevation.IsElevated())
            Skip = "Requires Administrator (VHDX attach). Run: right-click PowerShell > Run as administrator, then 'dotnet test'.";
    }
}
