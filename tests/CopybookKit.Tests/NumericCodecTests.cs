using System.Text;
using CopybookKit.Decoding;
using static CopybookKit.Tests.TestData;

namespace CopybookKit.Tests;

public class NumericCodecTests
{
    [Theory]
    [InlineData("F1F2F3", SignPosition.None, 123, false)]
    [InlineData("F0F0F0", SignPosition.None, 0, false)]
    [InlineData("F1F2C3", SignPosition.None, 123, false)]
    [InlineData("F1F2C3", SignPosition.TrailingEmbedded, 123, false)]
    [InlineData("F1F2D3", SignPosition.TrailingEmbedded, 123, true)]
    [InlineData("F1F2F3", SignPosition.TrailingEmbedded, 123, false)]
    [InlineData("F1F2A3", SignPosition.TrailingEmbedded, 123, false)]
    [InlineData("F1F2E3", SignPosition.TrailingEmbedded, 123, false)]
    [InlineData("F1F2B3", SignPosition.TrailingEmbedded, 123, true)]
    [InlineData("F0F0D0", SignPosition.TrailingEmbedded, 0, true)]
    [InlineData("D1F2F3", SignPosition.LeadingEmbedded, 123, true)]
    [InlineData("C1F2F3", SignPosition.LeadingEmbedded, 123, false)]
    [InlineData("60F1F2F3", SignPosition.LeadingSeparate, 123, true)]
    [InlineData("4EF1F2F3", SignPosition.LeadingSeparate, 123, false)]
    [InlineData("F1F2F360", SignPosition.TrailingSeparate, 123, true)]
    [InlineData("F1F2F34E", SignPosition.TrailingSeparate, 123, false)]
    public void Ebcdic_zoned_decimal_is_decoded(string hex, SignPosition sign, long magnitude, bool negative)
    {
        Assert.Equal(DecodeStatus.Ok, NumericCodec.Zoned(Hex(hex), sign, ebcdic: true, out UInt128 actual, out bool actualNegative));
        Assert.Equal((UInt128)magnitude, actual);
        Assert.Equal(negative, actualNegative);
    }

    [Theory]
    [InlineData("123", SignPosition.None, 123, false)]
    [InlineData("12C", SignPosition.TrailingEmbedded, 123, false)]
    [InlineData("12{", SignPosition.TrailingEmbedded, 120, false)]
    [InlineData("12I", SignPosition.TrailingEmbedded, 129, false)]
    [InlineData("12}", SignPosition.TrailingEmbedded, 120, true)]
    [InlineData("12J", SignPosition.TrailingEmbedded, 121, true)]
    [InlineData("12R", SignPosition.TrailingEmbedded, 129, true)]
    [InlineData("12p", SignPosition.TrailingEmbedded, 120, true)]
    [InlineData("12y", SignPosition.TrailingEmbedded, 129, true)]
    [InlineData("123", SignPosition.TrailingEmbedded, 123, false)]
    [InlineData("L23", SignPosition.LeadingEmbedded, 323, true)]
    [InlineData("-123", SignPosition.LeadingSeparate, 123, true)]
    [InlineData("+123", SignPosition.LeadingSeparate, 123, false)]
    [InlineData("123-", SignPosition.TrailingSeparate, 123, true)]
    [InlineData("123+", SignPosition.TrailingSeparate, 123, false)]
    public void Ascii_zoned_decimal_is_decoded(string text, SignPosition sign, long magnitude, bool negative)
    {
        Assert.Equal(
            DecodeStatus.Ok,
            NumericCodec.Zoned(Encoding.ASCII.GetBytes(text), sign, ebcdic: false, out UInt128 actual, out bool actualNegative));
        Assert.Equal((UInt128)magnitude, actual);
        Assert.Equal(negative, actualNegative);
    }

    [Theory]
    [InlineData("404040", SignPosition.None, true, "Blank")]
    [InlineData("000000", SignPosition.TrailingEmbedded, true, "Blank")]
    [InlineData("202020", SignPosition.None, false, "Blank")]
    [InlineData("F1F2D3", SignPosition.None, true, "Invalid")]
    [InlineData("F1C2F3", SignPosition.TrailingEmbedded, true, "Invalid")]
    [InlineData("F1F2FA", SignPosition.TrailingEmbedded, true, "Invalid")]
    [InlineData("F1F293", SignPosition.TrailingEmbedded, true, "Invalid")]
    [InlineData("40F1F2", SignPosition.None, true, "Invalid")]
    [InlineData("F1F2F3", SignPosition.LeadingSeparate, true, "Invalid")]
    [InlineData("4E", SignPosition.LeadingSeparate, true, "Invalid")]
    [InlineData("313233", SignPosition.None, true, "Invalid")]
    [InlineData("F1F2F3", SignPosition.None, false, "Invalid")]
    [InlineData("31327A", SignPosition.TrailingEmbedded, false, "Invalid")]
    [InlineData("203132", SignPosition.None, false, "Invalid")]
    [InlineData("", SignPosition.None, true, "Invalid")]
    public void Zoned_decimal_that_is_blank_or_wrong_is_reported(string hex, SignPosition sign, bool ebcdic, string expected)
    {
        Assert.Equal(Enum.Parse<DecodeStatus>(expected), NumericCodec.Zoned(Hex(hex), sign, ebcdic, out _, out _));
    }

    [Theory]
    [InlineData("12345C", 5, true, 12345, false)]
    [InlineData("12345D", 5, true, 12345, true)]
    [InlineData("12345F", 5, false, 12345, false)]
    [InlineData("12345A", 5, true, 12345, false)]
    [InlineData("12345E", 5, true, 12345, false)]
    [InlineData("12345B", 5, true, 12345, true)]
    [InlineData("01234C", 4, true, 1234, false)]
    [InlineData("0C", 1, true, 0, false)]
    [InlineData("0D", 1, true, 0, true)]
    [InlineData("0999999999999999999C", 18, true, 999999999999999999, false)]
    public void Packed_decimal_is_decoded(string hex, int digits, bool signed, long magnitude, bool negative)
    {
        Assert.Equal(DecodeStatus.Ok, NumericCodec.Packed(Hex(hex), digits, signed, out UInt128 actual, out bool actualNegative));
        Assert.Equal((UInt128)magnitude, actual);
        Assert.Equal(negative, actualNegative);
    }

    [Theory]
    [InlineData("1234567C", 4, true, "Invalid")] // a byte too long for four digits
    [InlineData("11234C", 4, true, "Invalid")] // the unused half byte is not zero
    [InlineData("1A345C", 5, true, "Invalid")]
    [InlineData("1234AC", 5, true, "Invalid")]
    [InlineData("123450", 5, true, "Invalid")]
    [InlineData("123459", 5, true, "Invalid")]
    [InlineData("12345D", 5, false, "Invalid")]
    [InlineData("000000", 5, true, "Blank")]
    [InlineData("404040", 5, true, "Blank")]
    [InlineData("202020", 5, true, "Blank")]
    public void Packed_decimal_that_is_blank_or_wrong_is_reported(string hex, int digits, bool signed, string expected)
    {
        Assert.Equal(Enum.Parse<DecodeStatus>(expected), NumericCodec.Packed(Hex(hex), digits, signed, out _, out _));
    }

    [Fact]
    public void Numbers_of_28_digits_keep_every_digit()
    {
        const string digits = "1234567890123456789012345678";
        UInt128 expected = UInt128.Parse(digits);

        Assert.Equal(DecodeStatus.Ok, NumericCodec.Zoned(Ebcdic(digits), SignPosition.None, true, out UInt128 zoned, out _));
        Assert.Equal(expected, zoned);

        Assert.Equal(DecodeStatus.Ok, NumericCodec.Packed(Hex("0" + digits + "D"), 28, true, out UInt128 packed, out bool negative));
        Assert.Equal(expected, packed);
        Assert.True(negative);

        Assert.True(NumericCodec.TryToDecimal(packed, negative, 10, out decimal value));
        Assert.Equal(-123456789012345678.9012345678m, value);
    }

    [Theory]
    [InlineData("7FFF", true, false, 32767, false)]
    [InlineData("8000", true, false, 32768, true)]
    [InlineData("FFFF", true, false, 1, true)]
    [InlineData("FFFF", false, false, 65535, false)]
    [InlineData("05F5E100", true, false, 100000000, false)]
    [InlineData("FA0A1F00", true, false, 100000000, true)]
    [InlineData("FA0A1F00", false, false, 4194967296, false)]
    [InlineData("0000000005F5E100", false, false, 100000000, false)]
    [InlineData("FFFFFFFFFA0A1F00", true, false, 100000000, true)]
    [InlineData("00E1F505", true, true, 100000000, false)]
    [InlineData("0100", true, true, 1, false)]
    [InlineData("FEFFFFFFFFFFFFFF", true, true, 2, true)]
    public void Binary_is_decoded_in_either_byte_order(string hex, bool signed, bool littleEndian, long magnitude, bool negative)
    {
        Assert.Equal(DecodeStatus.Ok, NumericCodec.Binary(Hex(hex), signed, littleEndian, out UInt128 actual, out bool actualNegative));
        Assert.Equal((UInt128)magnitude, actual);
        Assert.Equal(negative, actualNegative);
    }

    [Fact]
    public void Binary_covers_the_whole_range_of_eight_bytes()
    {
        Assert.Equal(DecodeStatus.Ok, NumericCodec.Binary(Hex("8000000000000000"), true, false, out UInt128 lowest, out bool negative));
        Assert.True(negative);
        Assert.True(NumericCodec.TryToInt64(lowest, negative, 0, out long value));
        Assert.Equal(long.MinValue, value);

        Assert.Equal(DecodeStatus.Ok, NumericCodec.Binary(Hex("FFFFFFFFFFFFFFFF"), false, false, out UInt128 highest, out _));
        Assert.Equal((UInt128)ulong.MaxValue, highest);
        Assert.False(NumericCodec.TryToInt64(highest, false, 0, out _));
        Assert.True(NumericCodec.TryToDecimal(highest, false, 0, out decimal wide));
        Assert.Equal(18446744073709551615m, wide);

        Assert.Equal(DecodeStatus.Invalid, NumericCodec.Binary(Hex("010203"), true, false, out _, out _));
    }

    [Theory]
    [InlineData("41100000", 1.0)]
    [InlineData("C1100000", -1.0)]
    [InlineData("40800000", 0.5)]
    [InlineData("42640000", 100.0)]
    [InlineData("C276A000", -118.625)]
    [InlineData("00000000", 0.0)]
    [InlineData("4110000000000000", 1.0)]
    [InlineData("413243F6A8885A31", 3.141592653589793)]
    [InlineData("C264000000000000", -100.0)]
    public void Ibm_hexadecimal_floating_point_is_decoded(string hex, double expected)
    {
        Assert.Equal(expected, NumericCodec.IbmFloat(Hex(hex)), 12);
    }

    [Fact]
    public void Ieee_floating_point_is_decoded_in_either_byte_order()
    {
        Assert.Equal(1.5, NumericCodec.IeeeFloat(Hex("3FC00000"), littleEndian: false));
        Assert.Equal(1.5, NumericCodec.IeeeFloat(Hex("0000C03F"), littleEndian: true));
        Assert.Equal(-2.25, NumericCodec.IeeeFloat(Hex("C002000000000000"), littleEndian: false));
        Assert.Equal(-2.25, NumericCodec.IeeeFloat(Hex("00000000000002C0"), littleEndian: true));
    }

    [Fact]
    public void A_scale_is_applied_without_losing_exactness()
    {
        Assert.True(NumericCodec.TryToDecimal(1234, false, 2, out decimal positive));
        Assert.Equal(12.34m, positive);
        Assert.Equal("12.34", positive.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(NumericCodec.TryToDecimal(5, true, 3, out decimal small));
        Assert.Equal(-0.005m, small);

        // 99PPP holds 12 as 12000.
        Assert.True(NumericCodec.TryToInt64(12, false, -3, out long scaledUp));
        Assert.Equal(12000, scaledUp);
        Assert.True(NumericCodec.TryToDecimal(12, true, -3, out decimal scaledUpDecimal));
        Assert.Equal(-12000m, scaledUpDecimal);

        Assert.False(NumericCodec.TryToDecimal(UInt128.MaxValue, false, 0, out _));
        Assert.False(NumericCodec.TryToInt64((UInt128)long.MaxValue + 2, true, 0, out _));
    }
}
