using System.Diagnostics;
using System.Text;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Parses every generated PowerShell script with PowerShell's own parser.
///
/// This matters more than it looks: the clean-whole-disk scripts run *after* diskpart has already
/// wiped the partition table, so a syntax error would surface only once the user's disk was empty.
/// Asserting on substrings cannot catch an unbalanced brace or a bad string escape; the parser can.
/// </summary>
public class GeneratedScriptSyntaxTests
{
    private const ulong GB = 1024UL * 1024 * 1024;

    public static TheoryData<string, string> Scripts()
    {
        var data = new TheoryData<string, string>();

        foreach (var scheme in Enum.GetValues<PartitionSchemeChoice>())
        foreach (var fs in new[] { FileSystemType.Exfat, FileSystemType.Ntfs, FileSystemType.Ext4 })
        {
            var clean = new FormatVolumeOperation(new FormatVolumeSettings
            {
                DiskNumber = 3,
                Scope = FormatScope.CleanWholeDisk,
                PartitionScheme = scheme,
                FileSystem = fs,
                // A label with an apostrophe is the classic way to break a single-quoted PS string.
                Label = "Bob's Disk",
                FullFormat = fs == FileSystemType.Ntfs
            });
            data.Add($"clean/{scheme}/{fs}", clean.PreviewScript(64 * GB));
        }

        var reformat = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = 3, Scope = FormatScope.ReformatPartition, PartitionNumber = 2,
            FileSystem = FileSystemType.Exfat, Label = "Bob's Disk"
        });
        data.Add("reformat/exfat", reformat.PreviewScript());

        var create = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = 3, OffsetBytes = 1024UL * 1024, SizeBytes = 8 * GB,
            FileSystem = FileSystemType.Ntfs, Label = "Bob's Disk", DriveLetter = "K"
        });
        data.Add("create/ntfs", create.PreviewScript());

        return data;
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void GeneratedScript_ParsesAsPowerShell(string name, string script)
    {
        // The Linux previews carry a "# then, inside WSL2:" mkfs line that is not PowerShell.
        var powershell = script.Split("# then, inside WSL2:")[0];

        // diskpart stanzas are not PowerShell either; they are recognisable by their first line.
        if (powershell.TrimStart().StartsWith("select disk", StringComparison.OrdinalIgnoreCase))
        {
            var split = powershell.IndexOf("exit", StringComparison.Ordinal);
            powershell = split >= 0 ? powershell[(split + 4)..] : "";
        }

        if (powershell.Trim().Length == 0) return;

        var errors = ParseErrors(powershell);
        Assert.True(errors.Length == 0, $"{name} does not parse:\n{errors}\n--- script ---\n{powershell}");
    }

    /// <summary>Runs the script through PowerShell's parser without executing a single statement.</summary>
    private static string ParseErrors(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$src = [Console]::In.ReadToEnd(); $errs = $null; " +
            "[void][System.Management.Automation.Language.Parser]::ParseInput($src, [ref]$null, [ref]$errs); " +
            "if ($errs) { $errs | ForEach-Object { $_.ToString() } }"));

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        process.StandardInput.Write(script);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        return output.Trim();
    }
}
