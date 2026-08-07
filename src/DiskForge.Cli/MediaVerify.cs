using System.Buffers.Binary;
using System.Diagnostics;
using DiskForge.Engine;
using DiskForge.Engine.Native;
using DiskForge.Engine.Operations;
using Serilog;

namespace DiskForge.Cli;

/// <summary>
/// Media verification — does this drive actually store what is written to it, and is it really the
/// size it claims?
///
/// This exists because the usual tools cannot reach the drives DiskForge is for. <c>f3</c> needs a
/// mounted writable filesystem, which Windows cannot provide for ext4/btrfs/xfs; <c>badblocks</c> needs
/// the raw device inside WSL, and WSL refuses to attach removable media at all. DiskForge already holds
/// raw handles to removable disks, so it is the one thing here that *can* do it.
///
/// Two modes:
/// <list type="bullet">
/// <item>read-only (default) — reads every sector and reports what cannot be read. Safe, but it can
/// only find unreadable media; it cannot prove the drive stores data faithfully.</item>
/// <item><c>--write</c> — the real test, and DESTRUCTIVE. Writes a position-encoded pattern over the
/// whole device and reads it back. Because every block carries its own offset, a block that returns
/// another block's contents is detected as <b>aliasing</b>, which is how counterfeit and worn-out flash
/// fails. This is the same principle as f3.</item>
/// </list>
/// </summary>
internal static class MediaVerify
{
    private const int ChunkBytes = 1024 * 1024;
    private const int BlockBytes = 4096;

    public static async Task<int> RunAsync(string[] args)
    {
        var diskNumber = IntArg(args, "--disk");
        var write = args.Contains("--write", StringComparer.OrdinalIgnoreCase);
        var confirmed = args.Contains("--yes", StringComparer.OrdinalIgnoreCase);

        if (diskNumber is null)
        {
            Console.Error.WriteLine("usage: diskforge verify-media --disk <n> [--write --yes]");
            Console.Error.WriteLine("  Default is a read-only surface scan.");
            Console.Error.WriteLine("  --write runs the full write/read-back test. DESTROYS ALL DATA on the disk,");
            Console.Error.WriteLine("  and is the only mode that can detect fake capacity or address aliasing.");
            return 2;
        }

        if (!Elevation.IsElevated())
        {
            Console.Error.WriteLine("Administrator is required for raw disk access. Re-run from an elevated shell.");
            return 3;
        }

        var inspector = new SystemInspector();
        var state = inspector.Capture(probeLinuxToolchain: false);
        var disk = state.FindDisk(diskNumber.Value);
        if (disk is null)
        {
            Console.Error.WriteLine($"Disk {diskNumber} was not found.");
            return 4;
        }

        // The same anti-wrong-target rule the write operations use. A surface test on the system disk
        // would be catastrophic in write mode and pointless in read mode.
        if (disk.IsSystemDisk || disk.IsBootDisk || state.SystemDiskNumber == disk.Number)
        {
            Console.Error.WriteLine($"Refusing to touch disk {disk.Number} — it is the system/boot disk.");
            return 5;
        }

        Console.WriteLine();
        Console.WriteLine($"Disk {disk.Number} — {disk.FriendlyName}");
        Console.WriteLine($"  {Size(disk.SizeBytes)} ({disk.SizeBytes:N0} bytes), {disk.Bus}, " +
                          $"{(disk.IsRemovable ? "removable" : "INTERNAL")}, sector {disk.LogicalSectorSize?.ToString() ?? "?"}");
        Console.WriteLine($"  mode: {(write ? "WRITE + READ-BACK (destructive)" : "read-only surface scan")}");

        if (write && !confirmed)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("--write destroys everything on this disk. Re-run with --yes to confirm.");
            return 6;
        }

        if (write && !disk.IsRemovable)
        {
            Console.Error.WriteLine("Refusing to write-test an INTERNAL disk.");
            return 7;
        }

        try
        {
            return write
                ? await WriteTestAsync(disk.Number, disk.SizeBytes).ConfigureAwait(false)
                : ReadScan(disk.Number, disk.SizeBytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("The test could not complete: " + ex.Message);
            Console.Error.WriteLine("A drive that disappears mid-test is itself a failure result.");
            Log.Error(ex, "Media verification failed on disk {Disk}", disk.Number);
            return 1;
        }
    }

    /// <summary>Reads every sector and reports the ranges that cannot be read.</summary>
    private static int ReadScan(int diskNumber, ulong sizeBytes)
    {
        using var handle = RawDiskAccess.OpenRead(diskNumber);
        var buffer = new byte[ChunkBytes];
        var total = (long)sizeBytes;
        var failures = new List<string>();
        var clock = Stopwatch.StartNew();

        Console.WriteLine();
        for (long offset = 0; offset < total; offset += ChunkBytes)
        {
            var want = (int)Math.Min(ChunkBytes, total - offset);
            try
            {
                ReadExact(handle, buffer, offset, want);
            }
            catch (Exception ex)
            {
                if (failures.Count < 40) failures.Add($"  read failed at {offset:N0} ({Size((ulong)offset)}): {ex.Message}");
            }
            Progress("reading", offset + want, total, clock);
        }

        Console.WriteLine();
        Console.WriteLine($"Read {Size(sizeBytes)} in {clock.Elapsed.TotalSeconds:N1}s " +
                          $"({Size((ulong)(total / Math.Max(1, clock.Elapsed.TotalSeconds)))}/s)");

        if (failures.Count == 0)
        {
            Console.WriteLine("Every sector was readable.");
            Console.WriteLine();
            Console.WriteLine("NOTE: this only proves the drive can be read. It cannot detect a drive that");
            Console.WriteLine("accepts writes and silently loses or aliases them — run with --write for that.");
            return 0;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"{failures.Count} unreadable region(s) — the media is faulty:");
        foreach (var f in failures) Console.Error.WriteLine(f);
        return 1;
    }

    /// <summary>
    /// Writes a position-encoded pattern over the whole disk, then reads it back. Every 4 KiB block
    /// carries its own byte offset, so a mismatch says which block's data came back — that is what
    /// distinguishes "this block is bad" from "this drive lies about its size and wraps around".
    /// </summary>
    private static async Task<int> WriteTestAsync(int diskNumber, ulong sizeBytes)
    {
        // Sector writes are refused underneath a mounted volume, so release them first.
        var inspector = new SystemInspector();
        var disk = inspector.Capture(probeLinuxToolchain: false).FindDisk(diskNumber);
        if (disk is not null)
            foreach (var line in DiskVolumeReleaser.Release(disk)) Console.WriteLine("  " + line);

        var seed = (ulong)Random.Shared.NextInt64();
        var total = (long)sizeBytes;
        var buffer = new byte[ChunkBytes];
        var clock = Stopwatch.StartNew();

        Console.WriteLine();
        Console.WriteLine($"  pattern seed 0x{seed:X16}");

        using (var target = RawDiskAccess.OpenWrite(diskNumber))
        {
            for (long offset = 0; offset < total; offset += ChunkBytes)
            {
                var want = (int)Math.Min(ChunkBytes, total - offset);
                FillPattern(buffer, offset, want, seed);
                RandomAccess.Write(target, buffer.AsSpan(0, want), offset);
                Progress("writing", offset + want, total, clock);
            }
            RandomAccess.FlushToDisk(target);
        }

        Console.WriteLine();
        Console.WriteLine("  flushed; reopening to defeat any caching…");

        // Reopen so the read cannot be served from a cache that would hide a device that never stored
        // the data. A real round trip through the controller is the whole point.
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        var expected = new byte[ChunkBytes];
        var bad = 0L;
        var aliased = 0L;
        var firstProblems = new List<string>();
        clock.Restart();
        _lastDecile = -1;

        using (var source = RawDiskAccess.OpenRead(diskNumber))
        {
            for (long offset = 0; offset < total; offset += ChunkBytes)
            {
                var want = (int)Math.Min(ChunkBytes, total - offset);
                FillPattern(expected, offset, want, seed);

                try
                {
                    ReadExact(source, buffer, offset, want);
                }
                catch (Exception ex)
                {
                    bad += want / BlockBytes;
                    if (firstProblems.Count < 20)
                        firstProblems.Add($"  {Size((ulong)offset)}: unreadable — {ex.Message}");
                    continue;
                }

                for (var b = 0; b + BlockBytes <= want; b += BlockBytes)
                {
                    if (buffer.AsSpan(b, BlockBytes).SequenceEqual(expected.AsSpan(b, BlockBytes))) continue;

                    var at = offset + b;
                    var claimed = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(b, 8));
                    var tag = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(b + 8, 8));

                    // A block that carries a *valid* record for a different offset means the device
                    // returned another location's data — the signature of aliasing / fake capacity.
                    if (tag == (seed ^ claimed) && claimed != (ulong)at)
                    {
                        aliased++;
                        if (firstProblems.Count < 20)
                            firstProblems.Add(
                                $"  {Size((ulong)at)}: ALIASED — returned the data written at {Size(claimed)}");
                    }
                    else
                    {
                        bad++;
                        if (firstProblems.Count < 20)
                            firstProblems.Add($"  {Size((ulong)at)}: data does not match what was written");
                    }
                }

                Progress("verifying", offset + want, total, clock);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Verified {Size(sizeBytes)} in {clock.Elapsed.TotalSeconds:N1}s");

        if (bad == 0 && aliased == 0)
        {
            // A sequential pass alone is not enough. A drive that passed exactly this test still
            // erased the blocks a filesystem format had just written to, seconds later, leaving them
            // 0xFF. Scattered small writes are what actually broke it, so test those too.
            var scatter = ScatterTest(diskNumber, total, seed, firstProblems);
            if (scatter == 0)
            {
                Console.WriteLine();
                Console.WriteLine("PASS — sequential and scattered writes both read back exactly.");
                Console.WriteLine("The media stores data faithfully at its full advertised capacity.");
                return 0;
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine("FAIL — the drive did not retain scattered writes.");
            Console.Error.WriteLine(
                "It passed a straight sequential pass, which is why a simple surface test is not " +
                "enough: real formatting writes small, scattered metadata, and that is what this " +
                "drive loses.");
            foreach (var p in firstProblems) Console.Error.WriteLine(p);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Replace the drive.");
            return 1;
        }

        Console.Error.WriteLine("FAIL — this drive does not reliably store what is written to it.");
        if (aliased > 0)
            Console.Error.WriteLine(
                $"  {aliased:N0} aliased block(s): the drive returned another location's data. " +
                "This is the signature of counterfeit or worn-out flash — its real capacity is smaller " +
                "than it reports.");
        if (bad > 0)
            Console.Error.WriteLine($"  {bad:N0} corrupt or unreadable block(s).");

        Console.Error.WriteLine();
        Console.Error.WriteLine("First problems:");
        foreach (var p in firstProblems) Console.Error.WriteLine(p);
        Console.Error.WriteLine();
        Console.Error.WriteLine("Replace the drive. Any filesystem corruption seen on it is explained by this.");
        return 1;
    }

    /// <summary>
    /// Writes many small blocks at scattered offsets, closes the handle, waits, then re-reads them —
    /// and also re-checks a broad sample of the sequential pattern that was <i>not</i> touched.
    ///
    /// This phase exists because of a drive that passed the sequential test and was still faulty: a
    /// filesystem format wrote its metadata into the first few MB, reported success, and seconds later
    /// those blocks read back as 0xFF (erased flash) while the rest of the drive was perfect. Real
    /// formatting is scattered small writes, not one long stream, so that is what has to be exercised.
    /// The untouched-sample check catches the other half of the same fault: a controller that erases a
    /// block somewhere else in response to a write here.
    ///
    /// Returns the number of problems found.
    /// </summary>
    private static int ScatterTest(int diskNumber, long total, ulong sequentialSeed, List<string> problems)
    {
        const int writes = 600;
        var scatterSeed = sequentialSeed ^ 0xA5A5_5A5A_A5A5_5A5AUL;
        var random = new Random(12345);          // deterministic, so a failure is reproducible
        var touched = new List<(long Offset, int Length)>(writes);
        var buffer = new byte[64 * 1024];

        Console.WriteLine();
        Console.WriteLine($"  scattered-write phase ({writes} small writes)…");

        using (var target = RawDiskAccess.OpenWrite(diskNumber))
        {
            for (var i = 0; i < writes; i++)
            {
                var length = BlockBytes * (1 + random.Next(16));               // 4 KiB … 64 KiB
                var offset = (long)random.NextInt64(0, (total - length) / BlockBytes) * BlockBytes;

                FillPattern(buffer, offset, length, scatterSeed);
                RandomAccess.Write(target, buffer.AsSpan(0, length), offset);
                touched.Add((offset, length));
            }
            RandomAccess.FlushToDisk(target);
        }

        // Close and reopen so nothing can be served from a cache that would hide a lost write.
        Thread.Sleep(TimeSpan.FromSeconds(5));

        var expected = new byte[buffer.Length];
        var failures = 0;

        using var source = RawDiskAccess.OpenRead(diskNumber);

        foreach (var (offset, length) in touched)
        {
            FillPattern(expected, offset, length, scatterSeed);
            try
            {
                ReadExact(source, buffer, offset, length);
            }
            catch (Exception ex)
            {
                failures++;
                if (problems.Count < 20) problems.Add($"  {Size((ulong)offset)}: unreadable — {ex.Message}");
                continue;
            }

            if (buffer.AsSpan(0, length).SequenceEqual(expected.AsSpan(0, length))) continue;

            failures++;
            if (problems.Count < 20)
            {
                var erased = buffer.AsSpan(0, length).IndexOfAnyExcept((byte)0xFF) < 0;
                problems.Add(erased
                    ? $"  {Size((ulong)offset)}: reads back as 0xFF — the flash block was erased and never rewritten"
                    : $"  {Size((ulong)offset)}: scattered write was not retained");
            }
        }

        // Did writing above disturb anything else? Sample the sequential pattern where untouched.
        for (long offset = 0; offset + BlockBytes <= total; offset += 4L * 1024 * 1024)
        {
            if (touched.Any(t => offset < t.Offset + t.Length && offset + BlockBytes > t.Offset)) continue;

            FillPattern(expected, offset, BlockBytes, sequentialSeed);
            try
            {
                ReadExact(source, buffer, offset, BlockBytes);
            }
            catch
            {
                failures++;
                continue;
            }

            if (buffer.AsSpan(0, BlockBytes).SequenceEqual(expected.AsSpan(0, BlockBytes))) continue;

            failures++;
            if (problems.Count < 20)
                problems.Add($"  {Size((ulong)offset)}: previously-verified data changed after writing elsewhere");
        }

        Console.WriteLine($"  scattered-write phase: {(failures == 0 ? "clean" : failures + " problem(s)")}");
        return failures;
    }

    /// <summary>
    /// Each 4 KiB block starts with its own offset and a seed-derived tag, repeated to fill the block.
    /// Encoding the position is what makes aliasing detectable rather than just "wrong data".
    /// </summary>
    private static void FillPattern(byte[] buffer, long offset, int length, ulong seed)
    {
        for (var b = 0; b < length; b += BlockBytes)
        {
            var blockOffset = (ulong)(offset + b);
            var span = buffer.AsSpan(b, Math.Min(BlockBytes, length - b));

            for (var i = 0; i + 16 <= span.Length; i += 16)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(span[i..], blockOffset);
                BinaryPrimitives.WriteUInt64LittleEndian(span[(i + 8)..], seed ^ blockOffset);
            }
        }
    }

    private static void ReadExact(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        byte[] buffer, long offset, int count)
    {
        var done = 0;
        while (done < count)
        {
            var n = RandomAccess.Read(handle, buffer.AsSpan(done, count - done), offset + done);
            if (n == 0) throw new IOException($"short read at {offset + done}");
            done += n;
        }
    }

    private static long _lastReport;
    private static int _lastDecile = -1;

    private static void Progress(string verb, long done, long total, Stopwatch clock)
    {
        var rate = done / Math.Max(0.001, clock.Elapsed.TotalSeconds);

        // Carriage-return redraw only works on a real console. Redirected to a file it produces one
        // line per update, which buried a 197-second scan under 190 lines of noise.
        if (Console.IsOutputRedirected)
        {
            var decile = (int)(10 * done / total);
            if (decile == _lastDecile) return;
            _lastDecile = decile;
            Console.WriteLine($"  {verb} {100.0 * done / total,5:N1}%  ({Size((ulong)rate)}/s)");
            return;
        }

        if (clock.ElapsedMilliseconds - _lastReport < 1000 && done < total) return;
        _lastReport = clock.ElapsedMilliseconds;
        Console.Write($"\r  {verb} {100.0 * done / total,5:N1}%  ({Size((ulong)rate)}/s)   ");
    }

    private static int? IntArg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : null;
    }

    private static string Size(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
