using Microsoft.Win32.SafeHandles;

namespace DiskForge.Engine.Linux.Ext;

/// <summary>
/// A <see cref="Stream"/> view of one partition on a physical disk, with the sector alignment the
/// device demands handled underneath.
///
/// Windows rejects reads and writes to <c>\\.\PhysicalDriveN</c> that are not whole sectors at sector
/// offsets, but a filesystem writer legitimately writes a 256-byte inode in the middle of a block. This
/// buffers an aligned window and does read-modify-write, so the formatter can write whatever shape it
/// likes while every actual device I/O stays aligned.
///
/// Positions are relative to the <b>start of the partition</b>, so the formatter never has to know
/// where on the disk it sits — and can never write outside it: seeking or writing past
/// <see cref="Length"/> throws.
/// </summary>
public sealed class RawPartitionStream : Stream
{
    /// <summary>Big enough that whole-block writes rarely straddle it, small enough to stay cheap.</summary>
    private const int WindowSize = 1024 * 1024;

    private readonly SafeFileHandle _handle;
    private readonly long _partitionOffset;
    private readonly long _length;
    private readonly int _sectorSize;
    private readonly byte[] _window;

    private long _windowStart = -1;   // partition-relative, sector-aligned
    private int _windowLength;
    private bool _windowDirty;
    private long _position;

    public RawPartitionStream(SafeFileHandle handle, long partitionOffset, long length, int sectorSize = 512)
    {
        if (partitionOffset % sectorSize != 0)
            throw new ArgumentException($"Partition offset {partitionOffset} is not sector-aligned.");

        _handle = handle;
        _partitionOffset = partitionOffset;
        _length = length;
        _sectorSize = sectorSize;
        _window = new byte[WindowSize];
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _length)
                throw new ArgumentOutOfRangeException(nameof(value),
                    $"Position {value} is outside the partition (0..{_length}).");
            _position = value;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_position + buffer.Length > _length)
            throw new IOException(
                $"Refusing to write past the end of the partition " +
                $"(position {_position} + {buffer.Length} > {_length}).");

        while (!buffer.IsEmpty)
        {
            EnsureWindow(_position);

            var inWindow = (int)(_position - _windowStart);
            var take = Math.Min(buffer.Length, _windowLength - inWindow);

            buffer[..take].CopyTo(_window.AsSpan(inWindow, take));
            _windowDirty = true;

            _position += take;
            buffer = buffer[take..];
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count && _position < _length)
        {
            EnsureWindow(_position);

            var inWindow = (int)(_position - _windowStart);
            var take = (int)Math.Min(Math.Min(count - total, _windowLength - inWindow), _length - _position);
            if (take <= 0) break;

            _window.AsSpan(inWindow, take).CopyTo(buffer.AsSpan(offset + total, take));
            _position += take;
            total += take;
        }
        return total;
    }

    /// <summary>Loads the aligned window containing <paramref name="position"/>, flushing the old one.</summary>
    private void EnsureWindow(long position)
    {
        if (_windowStart >= 0 && position >= _windowStart && position < _windowStart + _windowLength) return;

        FlushWindow();

        var start = position / _sectorSize * _sectorSize;
        var length = (int)Math.Min(WindowSize, _length - start);
        length = (int)AlignUp(length, _sectorSize);
        length = (int)Math.Min(length, AlignUp(_length - start, _sectorSize));

        ReadExact(_window.AsSpan(0, length), _partitionOffset + start);

        _windowStart = start;
        _windowLength = length;
        _windowDirty = false;
    }

    private void FlushWindow()
    {
        if (!_windowDirty || _windowStart < 0) return;

        RandomAccess.Write(_handle, _window.AsSpan(0, _windowLength), _partitionOffset + _windowStart);
        _windowDirty = false;
    }

    private void ReadExact(Span<byte> destination, long deviceOffset)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var n = RandomAccess.Read(_handle, destination[total..], deviceOffset + total);
            if (n == 0)
            {
                // Past the end of the device: treat as zeros rather than failing, so a partition that
                // ends mid-window still works.
                destination[total..].Clear();
                return;
            }
            total += n;
        }
    }

    public override void Flush()
    {
        FlushWindow();
        RandomAccess.FlushToDisk(_handle);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return _position;
    }

    public override void SetLength(long value)
        => throw new NotSupportedException("A partition's length is fixed.");

    protected override void Dispose(bool disposing)
    {
        if (disposing) Flush();
        base.Dispose(disposing);
    }

    private static long AlignUp(long value, int multiple)
    {
        var rem = value % multiple;
        return rem == 0 ? value : value + (multiple - rem);
    }
}
