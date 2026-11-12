namespace CopybookKit.Reading;

/// <summary>Cuts a stream into records. The record returned stays valid until the next read.</summary>
internal abstract class RecordSource(Stream stream, bool leaveOpen) : IDisposable
{
    protected Stream Stream { get; } = stream;

    /// <summary>The 1-based number of the record being read, for error messages.</summary>
    protected long RecordNumber { get; private set; }

    public bool TryRead(out ReadOnlyMemory<byte> record)
    {
        RecordNumber++;
        return TryReadCore(out record);
    }

    public void Dispose()
    {
        if (!leaveOpen)
        {
            Stream.Dispose();
        }
    }

    protected abstract bool TryReadCore(out ReadOnlyMemory<byte> record);

    /// <summary>Fills the buffer unless the stream ends first, and returns the bytes read.</summary>
    protected int Fill(Span<byte> buffer) => Stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
}

/// <summary>RECFM=F or FB: records of one length, back to back.</summary>
internal sealed class FixedRecordSource : RecordSource
{
    private readonly byte[] _record;

    public FixedRecordSource(Stream stream, int recordLength, bool leaveOpen)
        : base(stream, leaveOpen)
    {
        if (recordLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(recordLength), recordLength, "The record length must be at least 1.");
        }

        // Found before the first record is read, so a wrong length doesn't produce records of garbage first.
        if (stream.CanSeek && (stream.Length - stream.Position) % recordLength is var leftOver and not 0)
        {
            throw new RecordFormatException(
                $"The file has {stream.Length - stream.Position} bytes, which is not a whole number of {recordLength}-byte records: " +
                $"{leftOver} bytes are left over. Check the record length and the record format.");
        }

        _record = new byte[recordLength];
    }

    protected override bool TryReadCore(out ReadOnlyMemory<byte> record)
    {
        int read = Fill(_record);
        if (read == 0)
        {
            record = default;
            return false;
        }

        if (read < _record.Length)
        {
            throw new RecordFormatException(
                $"Record {RecordNumber} has only {read} bytes, but every record should have {_record.Length}. " +
                "Check the record length and the record format.");
        }

        record = _record;
        return true;
    }
}

/// <summary>
/// RECFM=V or VB with the record descriptor words kept: each record starts with a 2-byte
/// big-endian length that counts the 4-byte descriptor itself, then 2 zero bytes.
/// </summary>
internal sealed class RdwRecordSource(Stream stream, bool leaveOpen) : RecordSource(stream, leaveOpen)
{
    private const int DescriptorLength = 4;
    private readonly byte[] _descriptor = new byte[DescriptorLength];
    private byte[] _record = new byte[1024];

    protected override bool TryReadCore(out ReadOnlyMemory<byte> record)
    {
        int read = Fill(_descriptor);
        if (read == 0)
        {
            record = default;
            return false;
        }

        int length = (_descriptor[0] << 8) | _descriptor[1];
        if (read < DescriptorLength || length < DescriptorLength || _descriptor[2] != 0 || _descriptor[3] != 0)
        {
            throw new RecordFormatException(
                $"Record {RecordNumber} doesn't start with a valid record descriptor word " +
                $"({Convert.ToHexString(_descriptor.AsSpan(0, read))}). The file may not be in variable-length format, " +
                "or it was transferred without its descriptor words.");
        }

        int dataLength = length - DescriptorLength;
        if (_record.Length < dataLength)
        {
            _record = new byte[dataLength];
        }

        read = Fill(_record.AsSpan(0, dataLength));
        if (read < dataLength)
        {
            throw new RecordFormatException(
                $"Record {RecordNumber} should have {dataLength} bytes of data, but the file ends after {read}.");
        }

        record = _record.AsMemory(0, dataLength);
        return true;
    }
}

/// <summary>
/// One record per line. Lines shorter than the layout are padded with spaces, because
/// editors and file transfers often strip trailing spaces. Empty lines are skipped.
/// </summary>
internal sealed class LineRecordSource(Stream stream, int minimumLength, int maxLineLength, byte space, bool leaveOpen)
    : RecordSource(stream, leaveOpen)
{
    private readonly byte[] _chunk = new byte[1 << 16];
    private byte[] _line = new byte[Math.Max(minimumLength, 256)];
    private int _chunkStart;
    private int _chunkEnd;
    private bool _ended;

    protected override bool TryReadCore(out ReadOnlyMemory<byte> record)
    {
        while (true)
        {
            int length = ReadLine(out bool any);
            if (!any)
            {
                record = default;
                return false;
            }

            if (length > 0 && _line[length - 1] == (byte)'\r')
            {
                length--;
            }

            if (length == 0)
            {
                continue;
            }

            if (length < minimumLength)
            {
                _line.AsSpan(length, minimumLength - length).Fill(space);
                length = minimumLength;
            }

            record = _line.AsMemory(0, length);
            return true;
        }
    }

    /// <summary>Copies the next line, without its line feed, into the line buffer.</summary>
    private int ReadLine(out bool any)
    {
        int length = 0;
        any = false;
        while (true)
        {
            if (_chunkStart == _chunkEnd)
            {
                if (_ended)
                {
                    return length;
                }

                _chunkStart = 0;
                _chunkEnd = Stream.Read(_chunk, 0, _chunk.Length);
                if (_chunkEnd == 0)
                {
                    _ended = true;
                    return length;
                }
            }

            any = true;
            ReadOnlySpan<byte> available = _chunk.AsSpan(_chunkStart, _chunkEnd - _chunkStart);
            int newline = available.IndexOf((byte)'\n');
            int take = newline < 0 ? available.Length : newline;
            // A line longer than the limit is taken as a sign that the file isn't text.
            if (take > maxLineLength - length)
            {
                throw new RecordFormatException(
                    $"Record {RecordNumber} is longer than {maxLineLength:N0} bytes without a line break. " +
                    "The file is probably not a text file. Check the record format.");
            }

            if (_line.Length < length + take)
            {
                Array.Resize(ref _line, (int)Math.Min(Math.Max(length + take, (long)_line.Length * 2), Array.MaxLength));
            }

            available[..take].CopyTo(_line.AsSpan(length));
            length += take;
            _chunkStart += newline < 0 ? take : take + 1;
            if (newline >= 0)
            {
                return length;
            }
        }
    }
}
