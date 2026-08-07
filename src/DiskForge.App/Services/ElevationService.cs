using System.Diagnostics;
using System.Windows;
using Serilog;

namespace DiskForge.App.Services;

/// <summary>Relaunches DiskForge elevated (UAC) so write operations can run. Fails closed on cancel.</summary>
public static class ElevationService
{
    public static bool TryRestartAsAdministrator()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Log.Warning("Cannot determine executable path for elevation");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            Application.Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            // User declined the UAC prompt, or elevation failed — stay running, unelevated.
            Log.Information(ex, "Elevation request was cancelled or failed");
            return false;
        }
    }
}
