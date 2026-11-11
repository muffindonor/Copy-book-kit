using System.Text;

namespace CopybookKit.Decoding;

/// <summary>Turns the bytes of a text field into characters, trimming the padding.</summary>
internal sealed class TextDecoder
{
    private readonly Encoding _encoding;
    private readonly TextTrim _trim;

    /// <summary>One character per byte value, for the single-byte code pages mainframes use.</summary>
    private readonly char[]? _table;

    private char[] _buffer = new char[256];

    public TextDecoder(Encoding encoding, TextTrim trim)
    {
        _encoding = encoding;
        _trim = trim;
        if (encoding.IsSingleByte)
        {
            byte[] all = new byte[256];
            for (int i = 0; i < all.Length; i++)
            {
                all[i] = (byte)i;
            }

            char[] table = encoding.GetChars(all);
            if (table.Length == 256)
            {
                _table = table;
            }
        }
    }

    /// <summary>
    /// Decodes a field. The result stays valid until the next call. An unset field is often
    /// zero bytes, so those are read as padding unless trimming is turned off.
    /// </summary>
    public ReadOnlySpan<char> Decode(ReadOnlySpan<byte> field)
    {
        if (_buffer.Length < field.Length)
        {
            _buffer = new char[Math.Max(field.Length, _buffer.Length * 2)];
        }

        int length;
        if (_table is { } table)
        {
            length = field.Length;
            for (int i = 0; i < field.Length; i++)
            {
                _buffer[i] = table[field[i]];
            }
        }
        else
        {
            // A single-byte character never decodes to more than one char, and a
            // multi-byte one never to more chars than it has bytes.
            length = _encoding.GetChars(field, _buffer);
        }

        Span<char> text = _buffer.AsSpan(0, length);
        if (_trim == TextTrim.None)
        {
            return text;
        }

        text.Replace('\0', ' ');
        return _trim == TextTrim.Both ? text.Trim(' ') : text.TrimEnd(' ');
    }
}
