using System.Security.Principal;

namespace DiskForge.Engine;

/// <summary>Administrator-privilege detection. Enumeration works unelevated; writes must not (§1.9).</summary>
public static class Elevation
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
