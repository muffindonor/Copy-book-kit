using System.Diagnostics;
using System.Text;
using CopybookKit.Decoding;

namespace CopybookKit.Tests;

/// <summary>
/// Copybooks and data files can come from someone else. These tests feed the library input
/// built to waste memory or time, or to smuggle something into its output.
/// </summary>
public class HardeningTests
{
    [Theory]
    [InlineData("PIC XX(2147483647)")]
    [InlineData("PIC X(2147483647)")]
    [InlineData("PIC 9(2147483647)")]
    [InlineData("PIC X(1048576)X")]
    [InlineData("PIC X(1048577)")]
    public void A_picture_that_expands_to_more_than_a_megabyte_is_refused(string picture)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        CopybookException error = Assert.Throws<CopybookException>(() => Copybook.Parse($"01 R.\n 05 F {picture}."));

        Assert.Contains("is too long", error.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 << 20, "Refusing the picture reserved a lot of memory.");
    }

    [Theory]
    [InlineData("PIC X(+5)")]
    [InlineData("PIC X( 5)")]
    [InlineData("PIC X(0)")]
    [InlineData("PIC X(-1)")]
    [InlineData("PIC X(5")]
    public void A_repeat_count_must_be_plain_digits(string picture)
    {
        CopybookException error = Assert.Throws<CopybookException>(() => Copybook.Parse($"01 R.\n 05 F {picture}.", SourceFormat.Free));

        Assert.Equal(2, error.Line);
    }

    [Theory]
    [InlineData("01 R.\n 05 FILLER PIC X OCCURS 2000000000 TIMES.")]
    [InlineData("01 R.\n 05 G OCCURS 2000000 TIMES.\n  10 FILLER PIC X.")]
    [InlineData("01 R.\n 05 FILLER OCCURS 1000 TIMES.\n  10 FILLER OCCURS 1000 TIMES.\n   15 FILLER PIC X.")]
    [InlineData("01 R.\n 05 F PIC X OCCURS 1000001 TIMES.")]
    public void A_table_that_expands_to_more_than_a_million_items_is_refused(string source)
    {
        CopybookException error = Assert.Throws<CopybookException>(() => Copybook.Parse(source));

        Assert.Contains("expands to more than", error.Message);
    }

    [Fact]
    public void A_large_filler_table_is_stepped_over_and_the_field_after_it_is_found()
    {
        Copybook copybook = Copybook.Parse("01 R.\n 05 A PIC X.\n 05 FILLER PIC X OCCURS 900000 TIMES.\n 05 B PIC X(2).");
        byte[] data = new byte[900_003];
        Array.Fill(data, (byte)'.');
        data[0] = (byte)'a';
        data[^2] = (byte)'b';
        data[^1] = (byte)'c';

        using var reader = new CopybookReader(new MemoryStream(data), copybook, new ReadOptions { Encoding = Encoding.ASCII });
        Assert.True(reader.Read());

        Assert.Equal("a", reader.GetString("A"));
        Assert.Equal("bc", reader.GetString("B"));
        Assert.Equal("""{"A":"a","B":"bc"}""", reader.ToJson());
    }

    [Theory]
    [InlineData(RecordFormat.Fixed)]
    [InlineData(RecordFormat.LineSequential)]
    public void A_layout_longer_than_the_limit_is_refused_before_any_memory_is_reserved(RecordFormat format)
    {
        // 1,000 fields of a megabyte each: a 150-byte copybook that asks for a gigabyte per record.
        Copybook copybook = Copybook.Parse("01 R.\n 05 F PIC X(1000000) OCCURS 1000 TIMES.");
        long before = GC.GetAllocatedBytesForCurrentThread();

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new CopybookReader(new MemoryStream(), copybook, new ReadOptions { Format = format }));

        Assert.Contains("MaxRecordLength", error.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 << 20, "Refusing the layout reserved a lot of memory.");
    }

    [Fact]
    public void The_record_length_limit_can_be_changed()
    {
        Copybook copybook = Copybook.Parse("01 R.\n 05 F PIC X(20).");
        byte[] data = Encoding.ASCII.GetBytes("12345678901234567890");

        Assert.Throws<ArgumentException>(
            () => new CopybookReader(new MemoryStream(data), copybook, new ReadOptions { Encoding = Encoding.ASCII, MaxRecordLength = 19 }));

        using var reader = new CopybookReader(new MemoryStream(data), copybook, new ReadOptions { Encoding = Encoding.ASCII, MaxRecordLength = 20 });
        Assert.True(reader.Read());
        Assert.Equal("12345678901234567890", reader.GetString("F"));
    }

    [Fact]
    public void A_text_line_longer_than_the_limit_is_refused()
    {
        using var reader = new CopybookReader(
            new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 500) + "\n")),
            Copybook.Parse("01 R.\n 05 F PIC X(4)."),
            new ReadOptions { Format = RecordFormat.LineSequential, Encoding = Encoding.ASCII, MaxRecordLength = 100 });

        RecordFormatException error = Assert.Throws<RecordFormatException>(() => reader.Read());
        Assert.Contains("without a line break", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_record_length_below_1_is_refused(int length)
    {
        Copybook copybook = Copybook.Parse("01 R.\n 05 F PIC X(4).");

        foreach (RecordFormat format in Enum.GetValues<RecordFormat>())
        {
            Assert.ThrowsAny<ArgumentException>(
                () => new CopybookReader(new MemoryStream(), copybook, new ReadOptions { Format = format, RecordLength = length }));
        }
    }

    [Fact]
    public void Scaling_a_number_up_never_wraps_around()
    {
        // 2^127 times ten no longer fits 128 bits. It must be reported, not returned as a small wrong number.
        UInt128 large = UInt128.One << 127;

        Assert.False(NumericCodec.TryToDecimal(large, negative: false, scale: -1, out _));
        Assert.False(NumericCodec.TryToInt64(large, negative: false, scale: -1, out _));
        Assert.False(NumericCodec.TryToInt64(UInt128.MaxValue / 10, negative: true, scale: -3, out _));
        Assert.True(NumericCodec.TryToInt64(12, negative: true, scale: -2, out long value));
        Assert.Equal(-1200, value);
    }

    [Fact]
    public void Control_characters_in_a_copybook_never_reach_names_or_messages()
    {
        const string Escape = "\u001b[31m";
        Copybook copybook = Copybook.Parse($"01 R.\n 05 G\u0007\u009b PIC X.\n 05\u001bH PIC X.\n 05 I\0 PIC X.", SourceFormat.Free);

        Assert.Equal(new[] { "R", "G", "H", "I" }, copybook.Items.Select(item => item.Name));
        Assert.All(copybook.Items, item => Assert.DoesNotContain(item.Name, char.IsControl));
        Assert.All(copybook.Items, item => Assert.DoesNotContain(item.ToString(), char.IsControl));

        CopybookException error = Assert.Throws<CopybookException>(
            () => Copybook.Parse($"01 R.\n 05 F PIC X WRONG{Escape}WORD.", SourceFormat.Free));
        Assert.DoesNotContain(error.Message, char.IsControl);
    }

    [Fact]
    public void A_copybook_cannot_fake_a_continuation_line_with_a_control_character()
    {
        // U+0001 is the marker the parser uses internally for a continued line.
        Copybook copybook = Copybook.Parse("01 R.\n 05 A PIC X(3).\n\u000105 B PIC X(2).", SourceFormat.Free);

        Assert.Equal(new[] { "R", "A", "B" }, copybook.Items.Select(item => item.Name));
    }

    [Fact]
    public void An_item_from_another_copybook_is_refused()
    {
        Copybook one = Copybook.Parse("01 R.\n 05 A PIC X.");
        Copybook other = Copybook.Parse("01 R.\n 05 FILLER PIC X(5).\n 05 A PIC X.\n 05 B PIC X OCCURS 9 TIMES.");
        using CopybookReader reader = TestData.AsciiReader("01 R.\n 05 A PIC X.", "x");
        Assert.True(reader.Read());

        Assert.Equal("x", reader.GetValue(reader.Copybook.Find("A")));
        Assert.Throws<ArgumentException>(() => reader.GetValue(one.Find("A")));
        Assert.Throws<ArgumentException>(() => reader.GetValue(other.Find("A")));
        Assert.Throws<ArgumentException>(() => reader.GetValue(other.Find("B"), 9));
    }

    [Fact]
    public void Csv_can_protect_a_spreadsheet_from_text_that_looks_like_a_formula()
    {
        const string Layout = "01 R.\n 05 A PIC X(12).\n 05 B PIC S9(3) SIGN LEADING SEPARATE.\n 05 C PIC X(4).";
        const string Data = "=HYPERLINK()-042@SUM\n+1+1        +007 ok \n";

        var plain = new StringWriter();
        using (CopybookReader reader = Lines(Layout, Data))
        {
            reader.WriteCsv(plain);
        }

        var guarded = new StringWriter();
        using (CopybookReader reader = Lines(Layout, Data))
        {
            reader.WriteCsv(guarded, limit: null, protectFormulas: true);
        }

        Assert.Equal("A,B,C\r\n=HYPERLINK(),-42,@SUM\r\n+1+1,7,\" ok\"\r\n", plain.ToString());

        // Text is marked. The number -42 is a number, so it stays one.
        Assert.Equal("A,B,C\r\n'=HYPERLINK(),-42,'@SUM\r\n'+1+1,7,\" ok\"\r\n", guarded.ToString());
    }

    [Fact]
    public void A_bad_counter_inside_an_alternative_layout_points_to_selectors()
    {
        const string Layout = """
            01 R.
              05 KIND PIC X.
              05 NAME PIC X(6).
              05 LIST REDEFINES NAME.
                10 HOW-MANY PIC 9.
                10 ENTRY PIC X OCCURS 0 TO 5 TIMES DEPENDING ON HOW-MANY.
            """;

        using CopybookReader reader = Lines(Layout, "NSMITH \n");
        Assert.True(reader.Read());
        RecordFormatException error = Assert.Throws<RecordFormatException>(() => reader.ToJson());
        Assert.Contains("LIST is one of several layouts", error.Message);
        Assert.Contains("selector", error.Message);

        var options = new ReadOptions { Encoding = Encoding.ASCII, Format = RecordFormat.LineSequential };
        options.Selectors.Add(new RedefineRule("KIND", new Dictionary<string, string> { ["N"] = "NAME", ["L"] = "LIST" }));
        using var selecting = new CopybookReader(new MemoryStream(Encoding.ASCII.GetBytes("NSMITH \nL2ab\n")), Copybook.Parse(Layout), options);
        Assert.True(selecting.Read());
        Assert.Equal("""{"KIND":"N","NAME":"SMITH"}""", selecting.ToJson());
        Assert.True(selecting.Read());
        Assert.Equal("""{"KIND":"L","LIST":{"HOW-MANY":2,"ENTRY":["a","b"]}}""", selecting.ToJson());
    }

    [Fact]
    public void A_record_that_fails_part_way_is_not_counted_twice_when_read_again()
    {
        const string Layout = "01 R.\n 05 BAD PIC 9(2).\n 05 HOW-MANY PIC 9.\n 05 ENTRY PIC X OCCURS 0 TO 5 TIMES DEPENDING ON HOW-MANY.";
        using CopybookReader reader = Lines(Layout, "xx9abc\n");
        Assert.True(reader.Read());

        Assert.Throws<RecordFormatException>(() => reader.GetValues());
        Assert.Throws<RecordFormatException>(() => reader.ToJson());
        Assert.Throws<RecordFormatException>(() => reader.GetValues());

        Assert.Equal(1, reader.Statistics.InvalidFields);
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-8")]
    [InlineData("utf-32")]
    public void Text_in_a_multi_byte_encoding_never_overruns_its_buffer(string encodingName)
    {
        // 300 bytes that are not valid in any of these encodings, in a field with an odd length.
        byte[] data = new byte[301];
        Array.Fill(data, (byte)0xFF);
        data[^1] = 0xD8;
        var options = new ReadOptions { Encoding = Encoding.GetEncoding(encodingName), Trim = TextTrim.None };

        using var reader = new CopybookReader(new MemoryStream(data), Copybook.Parse("01 R.\n 05 F PIC X(301)."), options);
        Assert.True(reader.Read());

        Assert.NotNull(reader.GetString("F"));
    }

    [Fact]
    public void Deeply_nested_and_very_long_copybooks_parse_quickly()
    {
        var source = new StringBuilder("01 R.\n");
        for (int level = 2; level <= 48; level++)
        {
            source.Append(' ', level).Append($"{level:00} G{level}.\n");
        }

        for (int i = 0; i < 200_000; i++)
        {
            source.Append($"   49 F{i} PIC X VALUE 'it''s'.\n");
        }

        var watch = Stopwatch.StartNew();
        Copybook copybook = Copybook.Parse(source.ToString(), SourceFormat.Free);

        Assert.Equal(200_000, copybook.Length);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Parsing took {watch.Elapsed.TotalSeconds:F1} s.");
    }

    [Fact]
    public void A_floating_point_value_a_decimal_cannot_hold_is_refused_not_converted()
    {
        byte[] data = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(data, 1.5);
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(data.AsSpan(8), double.PositiveInfinity);
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(data.AsSpan(16), 1e300);
        var options = new ReadOptions { FloatFormat = FloatFormat.Ieee };

        using var reader = new CopybookReader(
            new MemoryStream(data), Copybook.Parse("01 R.\n 05 A COMP-2.\n 05 B COMP-2.\n 05 C COMP-2."), options);
        Assert.True(reader.Read());

        Assert.Equal(1.5m, reader.GetDecimal("A"));
        Assert.Throws<InvalidCastException>(() => reader.GetDecimal("B"));
        Assert.Throws<InvalidCastException>(() => reader.GetDecimal("C"));
        Assert.Equal("""{"A":1.5,"B":null,"C":1E+300}""", reader.ToJson());
    }

    private static CopybookReader Lines(string copybook, string data) =>
        TestData.AsciiReader(copybook, data, new ReadOptions { Format = RecordFormat.LineSequential });
}
