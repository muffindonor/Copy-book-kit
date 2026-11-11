using System.Buffers.Binary;

namespace CopybookKit.Decoding;

internal enum DecodeStatus
{
    Ok,

    /// <summary>The field holds only spaces or only zero bytes, as an unset field does.</summary>
    Blank,

    Invalid,
}

/// <summary>
/// Decodes COBOL numeric storage from raw bytes without allocating. Each method returns the
/// digits as an unscaled magnitude and a sign: 12.34 in a V99 field is 1234.
/// </summary>
internal static class NumericCodec
{
    private const byte EbcdicSpace = 0x40;
    private const byte AsciiSpace = 0x20;

    /// <summary>The most digits a <see cref="ulong"/> always holds.</summary>
    private const int MaxFastDigits = 19;

    public static DecodeStatus Zoned(ReadOnlySpan<byte> field, SignPosition sign, bool ebcdic, out UInt128 magnitude, out bool negative)
    {
        magnitude = 0;
        negative = false;
        if (field.IsEmpty)
        {
            return DecodeStatus.Invalid;
        }

        if (IsFilledWith(field, ebcdic ? EbcdicSpace : AsciiSpace) || IsFilledWith(field, 0))
        {
            return DecodeStatus.Blank;
        }

        ReadOnlySpan<byte> digits = field;
        int signIndex;
        switch (sign)
        {
            case SignPosition.LeadingSeparate:
                if (!TrySeparateSign(field[0], ebcdic, out negative))
                {
                    return DecodeStatus.Invalid;
                }

                digits = field[1..];
                signIndex = -1;
                break;

            case SignPosition.TrailingSeparate:
                if (!TrySeparateSign(field[^1], ebcdic, out negative))
                {
                    return DecodeStatus.Invalid;
                }

                digits = field[..^1];
                signIndex = -1;
                break;

            case SignPosition.LeadingEmbedded:
                signIndex = 0;
                break;

            default:
                // An unsigned field can still carry a positive sign zone in its last byte.
                signIndex = field.Length - 1;
                break;
        }

        if (digits.IsEmpty)
        {
            return DecodeStatus.Invalid;
        }

        byte zero = ebcdic ? (byte)0xF0 : (byte)'0';
        ulong fast = 0;
        UInt128 wide = 0;
        bool useWide = digits.Length > MaxFastDigits;
        for (int i = 0; i < digits.Length; i++)
        {
            uint digit;
            if (i == signIndex)
            {
                if (!TryOverpunch(digits[i], ebcdic, out digit, out negative))
                {
                    return DecodeStatus.Invalid;
                }
            }
            else
            {
                digit = (uint)(digits[i] - zero);
                if (digit > 9)
                {
                    return DecodeStatus.Invalid;
                }
            }

            if (useWide)
            {
                wide = (wide * 10) + digit;
            }
            else
            {
                fast = (fast * 10) + digit;
            }
        }

        if (negative && sign == SignPosition.None)
        {
            return DecodeStatus.Invalid;
        }

        magnitude = useWide ? wide : fast;
        return DecodeStatus.Ok;
    }

    /// <summary>
    /// COMP-3: two digits per byte, with the sign in the last half byte. An even digit count
    /// leaves the first half byte unused, and it must be zero. An invalid half byte is what a
    /// mainframe reports as a S0C7 data exception.
    /// </summary>
    public static DecodeStatus Packed(ReadOnlySpan<byte> field, int digits, bool signed, out UInt128 magnitude, out bool negative)
    {
        magnitude = 0;
        negative = false;
        if (field.Length != (digits / 2) + 1)
        {
            return DecodeStatus.Invalid;
        }

        if (IsFilledWith(field, 0) || IsFilledWith(field, EbcdicSpace) || IsFilledWith(field, AsciiSpace))
        {
            return DecodeStatus.Blank;
        }

        if (digits % 2 == 0 && (field[0] & 0xF0) != 0)
        {
            return DecodeStatus.Invalid;
        }

        ulong fast = 0;
        UInt128 wide = 0;
        bool useWide = digits > MaxFastDigits;
        for (int i = 0; i < field.Length - 1; i++)
        {
            uint high = (uint)field[i] >> 4;
            uint low = (uint)field[i] & 0x0F;
            if (high > 9 || low > 9)
            {
                return DecodeStatus.Invalid;
            }

            if (useWide)
            {
                wide = (wide * 100) + (high * 10) + low;
            }
            else
            {
                fast = (fast * 100) + (high * 10) + low;
            }
        }

        uint lastDigit = (uint)field[^1] >> 4;
        if (lastDigit > 9)
        {
            return DecodeStatus.Invalid;
        }

        switch (field[^1] & 0x0F)
        {
            case 0xC or 0xA or 0xE or 0xF:
                break;
            case 0xD or 0xB:
                negative = true;
                break;
            default:
                return DecodeStatus.Invalid;
        }

        if (negative && !signed)
        {
            return DecodeStatus.Invalid;
        }

        magnitude = useWide ? (wide * 10) + lastDigit : (UInt128)((fast * 10) + lastDigit);
        return DecodeStatus.Ok;
    }

    /// <summary>COMP: a two's complement integer of 2, 4 or 8 bytes.</summary>
    public static DecodeStatus Binary(ReadOnlySpan<byte> field, bool signed, bool littleEndian, out UInt128 magnitude, out bool negative)
    {
        magnitude = 0;
        negative = false;
        ulong bits;
        long value;
        switch (field.Length)
        {
            case 2:
                bits = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(field) : BinaryPrimitives.ReadUInt16BigEndian(field);
                value = (short)bits;
                break;
            case 4:
                bits = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(field) : BinaryPrimitives.ReadUInt32BigEndian(field);
                value = (int)bits;
                break;
            case 8:
                bits = littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(field) : BinaryPrimitives.ReadUInt64BigEndian(field);
                value = (long)bits;
                break;
            default:
                return DecodeStatus.Invalid;
        }

        if (signed && value < 0)
        {
            negative = true;
            magnitude = (ulong)(-(value + 1)) + 1;
        }
        else
        {
            magnitude = signed ? (ulong)value : bits;
        }

        return DecodeStatus.Ok;
    }

    /// <summary>
    /// IBM hexadecimal floating point, the mainframe's COMP-1 and COMP-2: a sign bit, a
    /// 7-bit power of 16 biased by 64, and a 24-bit or 56-bit fraction.
    /// </summary>
    public static double IbmFloat(ReadOnlySpan<byte> field)
    {
        bool negative = (field[0] & 0x80) != 0;
        int exponent = (field[0] & 0x7F) - 64;
        ulong fraction = 0;
        for (int i = 1; i < field.Length; i++)
        {
            fraction = (fraction << 8) | field[i];
        }

        double value = Math.ScaleB(fraction, (4 * exponent) - (8 * (field.Length - 1)));
        return negative ? -value : value;
    }

    public static double IeeeFloat(ReadOnlySpan<byte> field, bool littleEndian) => (field.Length, littleEndian) switch
    {
        (4, false) => BinaryPrimitives.ReadSingleBigEndian(field),
        (4, true) => BinaryPrimitives.ReadSingleLittleEndian(field),
        (_, false) => BinaryPrimitives.ReadDoubleBigEndian(field),
        (_, true) => BinaryPrimitives.ReadDoubleLittleEndian(field),
    };

    /// <summary>Applies the scale to an integer field. A negative scale multiplies the digits up.</summary>
    public static bool TryToInt64(UInt128 magnitude, bool negative, int scale, out long value)
    {
        value = 0;
        for (int i = scale; i < 0; i++)
        {
            magnitude *= 10;
        }

        if (negative)
        {
            if (magnitude > (UInt128)long.MaxValue + 1)
            {
                return false;
            }

            value = magnitude == (UInt128)long.MaxValue + 1 ? long.MinValue : -(long)magnitude;
            return true;
        }

        if (magnitude > (UInt128)long.MaxValue)
        {
            return false;
        }

        value = (long)magnitude;
        return true;
    }

    public static bool TryToDecimal(UInt128 magnitude, bool negative, int scale, out decimal value)
    {
        value = 0;
        for (int i = scale; i < 0; i++)
        {
            magnitude *= 10;
        }

        // decimal keeps a 96-bit magnitude.
        if (magnitude >> 96 != 0)
        {
            return false;
        }

        value = new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), (int)(uint)(magnitude >> 64), negative, (byte)Math.Max(scale, 0));
        return true;
    }

    private static bool IsFilledWith(ReadOnlySpan<byte> field, byte value) => !field.ContainsAnyExcept(value);

    private static bool TrySeparateSign(byte sign, bool ebcdic, out bool negative)
    {
        negative = sign == (ebcdic ? 0x60 : (byte)'-');
        return negative || sign == (ebcdic ? 0x4E : (byte)'+');
    }

    /// <summary>
    /// Reads a digit that shares its byte with the sign. In EBCDIC the high half byte is the
    /// sign: C, A, E and F are positive, D and B negative. In ASCII the same bytes arrive as
    /// the characters { A-I (positive) and } J-R (negative) after a code page conversion, or
    /// as p-y (negative) when written by a PC compiler such as GnuCOBOL or Micro Focus.
    /// </summary>
    private static bool TryOverpunch(byte value, bool ebcdic, out uint digit, out bool negative)
    {
        negative = false;
        if (ebcdic)
        {
            digit = (uint)value & 0x0F;
            switch (value >> 4)
            {
                case 0xF or 0xC or 0xA or 0xE:
                    return digit <= 9;
                case 0xD or 0xB:
                    negative = true;
                    return digit <= 9;
                default:
                    return false;
            }
        }

        switch (value)
        {
            case >= (byte)'0' and <= (byte)'9':
                digit = (uint)(value - '0');
                return true;
            case (byte)'{':
                digit = 0;
                return true;
            case >= (byte)'A' and <= (byte)'I':
                digit = (uint)(value - 'A' + 1);
                return true;
            case (byte)'}':
                digit = 0;
                negative = true;
                return true;
            case >= (byte)'J' and <= (byte)'R':
                digit = (uint)(value - 'J' + 1);
                negative = true;
                return true;
            case >= (byte)'p' and <= (byte)'y':
                digit = (uint)(value - 'p');
                negative = true;
                return true;
            default:
                digit = 0;
                return false;
        }
    }
}
