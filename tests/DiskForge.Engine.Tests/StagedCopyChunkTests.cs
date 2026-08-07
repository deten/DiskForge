using DiskForge.Engine.Linux;

namespace DiskForge.Engine.Tests;

/// <summary>
/// The staged-copy skip rule. This is the whole safety property of writing a filesystem image onto a
/// partition that already holds one: an all-zero chunk of the image may only be skipped when the drive
/// is already zero there.
///
/// The original code skipped on "the image is zero" alone, on the reasoning that a fresh filesystem is
/// mostly zeros. It is — but the *destination* is not, and only its first and last 1 MiB were wiped.
/// Every skipped chunk in between left the previous filesystem's bytes showing through the new one.
/// mkfs.xfs zeroes its log and unused metadata and then checksums what it wrote, so the damage appears
/// later as CRC errors on a filesystem whose superblock verified perfectly at creation.
/// </summary>
public class StagedCopyChunkTests
{
    private static byte[] Zeros(int n = 64) => new byte[n];

    private static byte[] Data(int n = 64, byte fill = 0xAB)
    {
        var b = new byte[n];
        Array.Fill(b, fill);
        return b;
    }

    [Fact]
    public void ImageWithContent_IsAlwaysCopied()
        => Assert.Equal(VhdxStagedFormatter.ChunkAction.CopyImage,
            VhdxStagedFormatter.DecideChunk(Data(), Zeros(), destinationReadable: true));

    [Fact]
    public void ImageWithContent_IsCopiedEvenOverExistingData()
        => Assert.Equal(VhdxStagedFormatter.ChunkAction.CopyImage,
            VhdxStagedFormatter.DecideChunk(Data(fill: 0x11), Data(fill: 0x22), destinationReadable: true));

    [Fact]
    public void BothBlank_IsSkipped()
        => Assert.Equal(VhdxStagedFormatter.ChunkAction.Skip,
            VhdxStagedFormatter.DecideChunk(Zeros(), Zeros(), destinationReadable: true));

    /// <summary>The regression: blank image over an old filesystem must erase, never skip.</summary>
    [Fact]
    public void BlankImageOverOldData_ErasesTheDestination()
        => Assert.Equal(VhdxStagedFormatter.ChunkAction.ZeroDestination,
            VhdxStagedFormatter.DecideChunk(Zeros(), Data(), destinationReadable: true));

    /// <summary>A single stale byte is enough — this is metadata, not free space.</summary>
    [Fact]
    public void BlankImageOverASingleStaleByte_ErasesTheDestination()
    {
        var destination = Zeros();
        destination[^1] = 0x01;

        Assert.Equal(VhdxStagedFormatter.ChunkAction.ZeroDestination,
            VhdxStagedFormatter.DecideChunk(Zeros(), destination, destinationReadable: true));
    }

    /// <summary>
    /// An unreadable destination is unknown, and unknown must never be treated as blank — that is the
    /// assumption that caused the bug in the first place, just arrived at a different way.
    /// </summary>
    [Fact]
    public void UnreadableDestination_IsZeroedRatherThanAssumedBlank()
        => Assert.Equal(VhdxStagedFormatter.ChunkAction.ZeroDestination,
            VhdxStagedFormatter.DecideChunk(Zeros(), Zeros(), destinationReadable: false));

    [Fact]
    public void NoChunkIsEverSkippedUnlessTheDestinationIsProvenBlank()
    {
        // Exhaustive over the four states that matter, asserting the one-way invariant directly.
        foreach (var imageBlank in new[] { true, false })
        foreach (var destinationBlank in new[] { true, false })
        foreach (var readable in new[] { true, false })
        {
            var action = VhdxStagedFormatter.DecideChunk(
                imageBlank ? Zeros() : Data(),
                destinationBlank ? Zeros() : Data(),
                readable);

            if (action == VhdxStagedFormatter.ChunkAction.Skip)
                Assert.True(imageBlank && destinationBlank && readable,
                    $"skipped with image blank={imageBlank}, destination blank={destinationBlank}, " +
                    $"readable={readable}");
        }
    }
}
