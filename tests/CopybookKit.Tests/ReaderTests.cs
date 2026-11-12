using System.Text;
using static CopybookKit.Tests.TestData;

namespace CopybookKit.Tests;

public class ReaderTests
{
    private const string Customer = """
        01 CUSTOMER.
           05 CUST-ID      PIC 9(4).
           05 CUST-NAME    PIC X(10).
           05 CUST-BALANCE PIC S9(5)V99 COMP-3.
           05 CUST-ORDERS  PIC S9(4) COMP.
        """;

    private static readonly byte[] Alice = Join(Ebcdic("0001ALICE     "), Hex("0012345C"), Hex("0003"));
    private static readonly byte[] Bob = Join(Ebcdic("0002BOB       "), Hex("0000150D"), Hex("FFFE"));

    [Fact]
    public void Fixed_length_records_are_read_in_order()
    {
        using CopybookReader reader = Reader(Customer, Join(Alice, Bob));

        Assert.Equal(0, reader.RecordNumber);
        Assert.True(reader.Read());
        Assert.Equal(1, reader.RecordNumber);
        Assert.Equal(1L, reader.GetValue("CUST-ID"));
        Assert.Equal("ALICE", reader.GetValue("CUST-NAME"));
        Assert.Equal(123.45m, reader.GetValue("CUST-BALANCE"));
        Assert.Equal(3L, reader.GetValue("CUST-ORDERS"));
        Assert.True(reader.RawRecord.SequenceEqual(Alice));

        Assert.True(reader.Read());
        Assert.Equal("BOB", reader.GetString("cust-name"));
        Assert.Equal(-1.50m, reader.GetDecimal("CUST-BALANCE"));
        Assert.Equal(-2, reader.GetInt64("CUST-ORDERS"));

        Assert.False(reader.Read());
        Assert.False(reader.Read());
        Assert.Equal(2, reader.RecordNumber);
        Assert.Equal(2, reader.Statistics.Records);
        Assert.Equal("CUSTOMER", reader.Record.Name);
    }

    [Fact]
    public void Values_keep_the_scale_of_the_picture()
    {
        using CopybookReader reader = Reader(Customer, Join(Ebcdic("0001ALICE     "), Hex("0010000C"), Hex("0003")));
        Assert.True(reader.Read());

        Assert.Equal("100.00", reader.GetString("CUST-BALANCE"));
        Assert.Equal("1", reader.GetString("CUST-ID"));
        Assert.Equal(100m, reader.GetDecimal("CUST-BALANCE"));
        Assert.Equal(100, reader.GetInt64("CUST-BALANCE"));
        Assert.Equal(1m, reader.GetDecimal("CUST-ID"));
    }

    [Fact]
    public void Typed_getters_refuse_values_they_cannot_represent()
    {
        using CopybookReader reader = Reader(Customer, Alice);
        Assert.True(reader.Read());

        Assert.Throws<InvalidCastException>(() => reader.GetDecimal("CUST-NAME"));
        Assert.Throws<InvalidCastException>(() => reader.GetInt64("CUST-NAME"));
        Assert.Throws<InvalidCastException>(() => reader.GetInt64("CUST-BALANCE"));
        Assert.Throws<ArgumentException>(() => reader.GetValue("CUSTOMER"));
        Assert.Throws<ArgumentException>(() => reader.GetValue("CUST-ID", 1));
        Assert.Throws<KeyNotFoundException>(() => reader.GetValue("NO-SUCH-FIELD"));
    }

    [Fact]
    public void There_is_no_record_before_the_first_read_or_after_the_last()
    {
        using CopybookReader reader = Reader(Customer, Alice);

        Assert.Throws<InvalidOperationException>(() => reader.GetValue("CUST-ID"));
        Assert.Throws<InvalidOperationException>(() => reader.ToJson());
        Assert.True(reader.Read());
        Assert.False(reader.Read());
        Assert.Throws<InvalidOperationException>(() => reader.GetValues());
        Assert.Throws<InvalidOperationException>(() => reader.RawRecord.Length);
    }

    [Fact]
    public void A_file_that_is_not_a_whole_number_of_records_is_refused_before_any_is_read()
    {
        RecordFormatException error = Assert.Throws<RecordFormatException>(() => Reader(Customer, Join(Alice, Bob[..7])));

        Assert.Contains("The file has 27 bytes, which is not a whole number of 20-byte records: 7 bytes are left over.", error.Message);
    }

    [Fact]
    public void A_stream_that_ends_inside_a_record_is_an_error_when_its_length_is_not_known()
    {
        using var reader = new CopybookReader(new ForwardOnlyStream(Join(Alice, Bob[..7])), Copybook.Parse(Customer));

        Assert.True(reader.Read());
        RecordFormatException error = Assert.Throws<RecordFormatException>(() => reader.Read());
        Assert.Contains("Record 2 has only 7 bytes", error.Message);
        Assert.Contains("should have 20", error.Message);
    }

    [Fact]
    public void The_record_length_can_be_longer_than_the_layout()
    {
        byte[] padded = Join(Alice, Ebcdic("XXXXX"), Bob, Ebcdic("YYYYY"));
        using CopybookReader reader = Reader(Customer, padded, new ReadOptions { RecordLength = 25 });

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal("BOB", reader.GetValue("CUST-NAME"));
        Assert.Equal(25, reader.RawRecord.Length);
        Assert.False(reader.Read());
    }

    [Fact]
    public void Fields_beyond_the_end_of_a_short_record_are_null_and_counted()
    {
        using CopybookReader reader = Reader(Customer, Alice[..16], new ReadOptions { RecordLength = 16 });
        Assert.True(reader.Read());

        Assert.Equal("ALICE", reader.GetValue("CUST-NAME"));
        Assert.Null(reader.GetValue("CUST-BALANCE"));
        Assert.Null(reader.GetDecimal("CUST-BALANCE"));
        Assert.Null(reader.GetInt64("CUST-ORDERS"));
        Assert.Null(reader.GetString("CUST-ORDERS"));
        Assert.Equal(2, reader.Statistics.MissingFields);
        Assert.Equal(0, reader.Statistics.InvalidFields);
    }

    [Fact]
    public void Variable_length_records_are_split_by_their_descriptor_words()
    {
        const string copybook = "01 R.\n 05 KIND PIC X.\n 05 BODY PIC X(8).";
        byte[] data = Join(
            Hex("000D0000"), Ebcdic("AFULLBODY"),
            Hex("00070000"), Ebcdic("BAB"),
            Hex("00040000"),
            Hex("00050000"), Ebcdic("C"));
        using CopybookReader reader = Reader(copybook, data, new ReadOptions { Format = RecordFormat.VariableRdw });

        Assert.True(reader.Read());
        Assert.Equal("FULLBODY", reader.GetValue("BODY"));
        Assert.True(reader.Read());
        Assert.Equal(3, reader.RawRecord.Length);
        Assert.Equal("B", reader.GetValue("KIND"));
        Assert.Null(reader.GetValue("BODY"));
        Assert.True(reader.Read());
        Assert.Equal(0, reader.RawRecord.Length);
        Assert.Null(reader.GetValue("KIND"));
        Assert.True(reader.Read());
        Assert.Equal("C", reader.GetValue("KIND"));
        Assert.False(reader.Read());
        Assert.Equal(4, reader.Statistics.Records);
    }

    [Theory]
    [InlineData("C1C2C3C4C5", "valid record descriptor word")] // text where a descriptor should be
    [InlineData("00030000", "valid record descriptor word")] // shorter than the descriptor itself
    [InlineData("0008", "valid record descriptor word")] // the file ends inside the descriptor
    [InlineData("000A0000C1C2", "ends after 2")]
    public void A_file_without_valid_descriptor_words_is_an_error(string hex, string message)
    {
        using CopybookReader reader = Reader("01 R.\n 05 F PIC X(6).", Hex(hex), new ReadOptions { Format = RecordFormat.VariableRdw });

        RecordFormatException error = Assert.Throws<RecordFormatException>(() => reader.Read());
        Assert.Contains("Record 1", error.Message);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Text_files_have_one_record_per_line()
    {
        const string copybook = "01 R.\n 05 CODE PIC X(3).\n 05 QTY PIC 9(3).\n 05 NOTE PIC X(6).";
        using CopybookReader reader = AsciiReader(
            copybook,
            "AAA001first \r\nBBB002\n\n\r\nCCC003a longer line than the layout\nDDD004last",
            new ReadOptions { Format = RecordFormat.LineSequential });

        var seen = new List<string>();
        while (reader.Read())
        {
            seen.Add($"{reader.GetValue("CODE")}/{reader.GetValue("QTY")}/{reader.GetValue("NOTE")}/{reader.RawRecord.Length}");
        }

        Assert.Equal(["AAA/1/first/12", "BBB/2//12", "CCC/3/a long/35", "DDD/4/last/12"], seen);
        Assert.Equal(0, reader.Statistics.MissingFields);
    }

    [Fact]
    public void A_line_longer_than_the_read_buffer_is_kept_whole()
    {
        string line = new string('A', 70_000) + "Z";
        using CopybookReader reader = AsciiReader(
            "01 R.\n 05 BIG PIC X(70001).",
            line + "\n" + line,
            new ReadOptions { Format = RecordFormat.LineSequential });

        Assert.True(reader.Read());
        Assert.Equal(line, reader.GetValue("BIG"));
        Assert.True(reader.Read());
        Assert.Equal(line, reader.GetValue("BIG"));
        Assert.False(reader.Read());
    }

    [Fact]
    public void An_empty_file_has_no_records()
    {
        foreach (RecordFormat format in Enum.GetValues<RecordFormat>())
        {
            using CopybookReader reader = Reader(Customer, [], new ReadOptions { Format = format });
            Assert.False(reader.Read());
            Assert.Equal(0, reader.Statistics.Records);
        }
    }

    [Fact]
    public void Tables_are_read_with_one_subscript_per_level()
    {
        const string copybook = """
            01 R.
               05 YEAR-NO PIC 9(4).
               05 QUARTERS OCCURS 2.
                  10 Q-NAME PIC X(2).
                  10 MONTHS PIC 9(2) OCCURS 3.
               05 TAGS PIC X OCCURS 4.
            """;
        using CopybookReader reader = AsciiReader(copybook, "2026Q1010203Q2040506WXYZ");
        Assert.True(reader.Read());

        Assert.Equal("Q1", reader.GetValue("Q-NAME", 1));
        Assert.Equal("Q2", reader.GetValue("Q-NAME", 2));
        Assert.Equal(1L, reader.GetValue("MONTHS", 1, 1));
        Assert.Equal(3L, reader.GetValue("MONTHS", 1, 3));
        Assert.Equal(4L, reader.GetValue("MONTHS", 2, 1));
        Assert.Equal(6L, reader.GetValue("MONTHS", 2, 3));
        Assert.Equal("W", reader.GetValue("TAGS", 1));
        Assert.Equal("Z", reader.GetValue("TAGS", 4));

        Assert.Throws<ArgumentException>(() => reader.GetValue("MONTHS", 1));
        Assert.Throws<ArgumentException>(() => reader.GetValue("MONTHS", 1, 4));
        Assert.Throws<ArgumentException>(() => reader.GetValue("MONTHS", 0, 1));
        Assert.Throws<ArgumentException>(() => reader.GetValue("TAGS"));

        Assert.Equal(
            ["YEAR-NO", "Q-NAME(1)", "MONTHS(1,1)", "MONTHS(1,2)", "MONTHS(1,3)", "Q-NAME(2)", "MONTHS(2,1)", "MONTHS(2,2)", "MONTHS(2,3)",
                "TAGS(1)", "TAGS(2)", "TAGS(3)", "TAGS(4)"],
            reader.Schema.Columns.Select(column => column.Name));
        Assert.Equal(
            [2026L, "Q1", 1L, 2L, 3L, "Q2", 4L, 5L, 6L, "W", "X", "Y", "Z"],
            reader.GetValues());
        Assert.Equal([2, 3], reader.Schema.Columns[8].Subscripts);
        Assert.Equal(typeof(long), reader.Schema.Columns[8].ClrType);
        Assert.Same(reader.Copybook.Find("MONTHS"), reader.Schema.Columns[8].Item);
        Assert.Equal(5, reader.Schema.IndexOf("q-name(2)"));
        Assert.Equal(-1, reader.Schema.IndexOf("Q-NAME"));
    }

    [Fact]
    public void A_variable_table_moves_the_fields_after_it()
    {
        const string copybook = """
            01 R.
               05 ITEM-COUNT PIC 9.
               05 ITEMS OCCURS 0 TO 3 DEPENDING ON ITEM-COUNT.
                  10 ITEM-CODE PIC X(2).
               05 TRAILER PIC X(3).
            """;
        using CopybookReader reader = AsciiReader(
            copybook,
            "2AABBEND\n0END\n3AABBCCEND\n",
            new ReadOptions { Format = RecordFormat.LineSequential, RecordLength = 1 });

        Assert.True(reader.Read());
        Assert.Equal(["AA", "BB", null, "END"], [reader.GetValue("ITEM-CODE", 1), reader.GetValue("ITEM-CODE", 2), reader.GetValue("ITEM-CODE", 3), reader.GetValue("TRAILER")]);
        Assert.Equal("""{"ITEM-COUNT":2,"ITEMS":[{"ITEM-CODE":"AA"},{"ITEM-CODE":"BB"}],"TRAILER":"END"}""", reader.ToJson());

        Assert.True(reader.Read());
        Assert.Null(reader.GetValue("ITEM-CODE", 1));
        Assert.Equal("END", reader.GetValue("TRAILER"));
        Assert.Equal("""{"ITEM-COUNT":0,"ITEMS":[],"TRAILER":"END"}""", reader.ToJson());

        Assert.True(reader.Read());
        Assert.Equal("CC", reader.GetValue("ITEM-CODE", 3));
        Assert.Equal("END", reader.GetValue("TRAILER"));
        Assert.Equal(0, reader.Statistics.MissingFields);
    }

    [Theory]
    [InlineData("4AABBCCDDEND", "which is 4")]
    [InlineData(" AABBEND", "no valid value")]
    [InlineData("XAABBEND", "no valid value")]
    public void A_table_count_the_copybook_does_not_allow_is_an_error(string record, string message)
    {
        const string copybook = """
            01 R.
               05 ITEM-COUNT PIC 9.
               05 ITEMS OCCURS 0 TO 3 DEPENDING ON ITEM-COUNT.
                  10 ITEM-CODE PIC X(2).
               05 TRAILER PIC X(3).
            """;
        using CopybookReader reader = AsciiReader(copybook, record, new ReadOptions { Format = RecordFormat.LineSequential, RecordLength = 1 });
        Assert.True(reader.Read());

        RecordFormatException error = Assert.Throws<RecordFormatException>(() => reader.GetValues());
        Assert.Contains("Record 1: ITEMS depends on ITEM-COUNT", error.Message);
        Assert.Contains(message, error.Message);
        Assert.Throws<RecordFormatException>(() => reader.ToJson());
    }

    [Fact]
    public void Invalid_numbers_become_null_and_are_counted_once()
    {
        byte[] bad = Join(Ebcdic("00X1ALICE     "), Hex("0012345C"), Hex("0003"));
        byte[] blank = Join(Ebcdic("    BOB       "), Hex("00000000"), Hex("0000"));
        using CopybookReader reader = Reader(Customer, Join(bad, blank));

        Assert.True(reader.Read());
        Assert.Null(reader.GetValue("CUST-ID"));
        Assert.Equal(123.45m, reader.GetValue("CUST-BALANCE"));
        Assert.Contains("\"CUST-ID\":null", reader.ToJson());
        _ = reader.GetValues();
        Assert.Equal(1, reader.Statistics.InvalidFields);
        Assert.Equal("Record 1: CUST-ID is not a valid zoned decimal number (bytes F0F0E7F1).", Assert.Single(reader.Statistics.Issues));

        // The same holds when the record is taken as JSON first and as values second.
        Assert.True(reader.Read());
        Assert.Contains("\"CUST-ID\":null", reader.ToJson());
        Assert.Null(reader.GetValue("CUST-ID"));
        Assert.Null(reader.GetValue("CUST-BALANCE"));
        Assert.Equal(0L, reader.GetValue("CUST-ORDERS"));
        Assert.Equal(1, reader.Statistics.InvalidFields);
        Assert.Equal(2, reader.Statistics.BlankNumericFields);
    }

    [Fact]
    public void Strict_mode_stops_at_the_first_invalid_number()
    {
        byte[] bad = Join(Ebcdic("0001ALICE     "), Hex("0012345C"), Hex("0003"), Ebcdic("0002BOB       "), Hex("00123456"), Hex("0003"));
        using CopybookReader reader = Reader(Customer, bad, new ReadOptions { InvalidData = InvalidDataPolicy.Throw });

        Assert.True(reader.Read());
        Assert.Equal(123.45m, reader.GetValue("CUST-BALANCE"));
        Assert.True(reader.Read());
        RecordFormatException error = Assert.Throws<RecordFormatException>(() => reader.GetValue("CUST-ID"));
        Assert.Equal("Record 2: CUST-BALANCE is not a valid packed decimal number (bytes 00123456).", error.Message);
        Assert.Throws<RecordFormatException>(() => reader.ToJson());
    }

    [Fact]
    public void Only_the_first_few_invalid_fields_are_described()
    {
        using CopybookReader reader = AsciiReader("01 R.\n 05 N PIC 9(2).", string.Concat(Enumerable.Repeat("X1", 50)));
        while (reader.Read())
        {
            Assert.Null(reader.GetValue("N"));
        }

        Assert.Equal(50, reader.Statistics.InvalidFields);
        Assert.Equal(20, reader.Statistics.Issues.Count);
        Assert.StartsWith("Record 20:", reader.Statistics.Issues[^1]);
    }

    [Theory]
    [InlineData(TextTrim.Right, "  AB")]
    [InlineData(TextTrim.Both, "AB")]
    [InlineData(TextTrim.None, "  AB  ")]
    public void Text_is_trimmed_as_asked(TextTrim trim, string expected)
    {
        using CopybookReader reader = AsciiReader("01 R.\n 05 T PIC X(6).", "  AB  ", new ReadOptions { Trim = trim });
        Assert.True(reader.Read());

        Assert.Equal(expected, reader.GetValue("T"));
    }

    [Fact]
    public void Zero_bytes_in_text_are_padding_unless_trimming_is_off()
    {
        byte[] data = Join(Ebcdic("AB"), Hex("000000"), Ebcdic(" "));

        using (CopybookReader reader = Reader("01 R.\n 05 T PIC X(6).", data))
        {
            Assert.True(reader.Read());
            Assert.Equal("AB", reader.GetValue("T"));
        }

        using (CopybookReader reader = Reader("01 R.\n 05 T PIC X(6).", data, new ReadOptions { Trim = TextTrim.None }))
        {
            Assert.True(reader.Read());
            Assert.Equal("AB\0\0\0 ", reader.GetValue("T"));
        }
    }

    [Fact]
    public void The_code_page_decides_what_the_bytes_mean()
    {
        const string copybook = "01 R.\n 05 T PIC X(3).";
        byte[] data = Hex("C1ADBD");

        Assert.Equal("A[]", ReadText(copybook, data, EbcdicEncodings.Cp1047));
        Assert.NotEqual("A[]", ReadText(copybook, data, EbcdicEncodings.Cp037));
        Assert.Equal("A[]", ReadText(copybook, Hex("C1BABB"), EbcdicEncodings.Cp037));
        Assert.Equal("אבג", ReadText(copybook, Hex("414243"), EbcdicEncodings.Cp424));
        Assert.Equal("€  ", ReadText(copybook, Hex("9F4040"), EbcdicEncodings.Cp1140));
        Assert.Equal("A1 ", ReadText(copybook, Hex("C1F140"), EbcdicEncodings.Cp500));
        Assert.Equal("hé", ReadText(copybook, [0x68, 0xC3, 0xA9], Encoding.UTF8));
    }

    [Theory]
    [InlineData("cp037", "IBM037")]
    [InlineData("CP1047", "IBM01047")]
    [InlineData("ibm500", "IBM500")]
    [InlineData("IBM-1140", "IBM01140")]
    [InlineData("37", "IBM037")]
    [InlineData("ebcdic", "IBM037")]
    [InlineData("cp424", "IBM424")]
    [InlineData("273", "IBM273")]
    [InlineData("ascii", "us-ascii")]
    [InlineData("utf-8", "utf-8")]
    [InlineData("latin1", "iso-8859-1")]
    public void Encodings_are_found_by_their_usual_names(string name, string webName)
    {
        Assert.Equal(webName, EbcdicEncodings.Parse(name).WebName, ignoreCase: true);
    }

    [Theory]
    [InlineData("cp99999")]
    [InlineData("klingon")]
    [InlineData(" ")]
    public void An_unknown_encoding_name_is_an_error(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => EbcdicEncodings.Parse(name));
    }

    [Fact]
    public void Binary_byte_order_and_float_format_follow_the_options()
    {
        const string copybook = "01 R.\n 05 B PIC S9(9) COMP-5.\n 05 F COMP-1.\n 05 D COMP-2.";

        using (CopybookReader reader = Reader(copybook, Hex("05F5E100 42640000 C264000000000000")))
        {
            Assert.True(reader.Read());
            Assert.Equal([100000000L, 100.0, -100.0], reader.GetValues());
        }

        var pc = new ReadOptions { BinaryByteOrder = ByteOrder.LittleEndian, FloatFormat = FloatFormat.Ieee };
        using (CopybookReader reader = Reader(copybook, Hex("00E1F505 0000C03F 00000000000002C0"), pc))
        {
            Assert.True(reader.Read());
            Assert.Equal([100000000L, 1.5, -2.25], reader.GetValues());
            Assert.Equal("""{"B":100000000,"F":1.5,"D":-2.25}""", reader.ToJson());
        }

        using (CopybookReader reader = Reader(copybook, Hex("05F5E100 3FC00000 C002000000000000"), new ReadOptions { Encoding = Encoding.ASCII }))
        {
            Assert.True(reader.Read());
            Assert.Equal([100000000L, 1.5, -2.25], reader.GetValues());
        }
    }

    [Fact]
    public void A_record_is_chosen_by_name_when_the_copybook_has_several()
    {
        const string copybook = "01 HEAD.\n 05 H-DATE PIC 9(8).\n01 DETAIL.\n 05 D-CODE PIC X(3).\n 05 D-QTY PIC 9(5).";

        using (CopybookReader reader = AsciiReader(copybook, "20261111"))
        {
            Assert.True(reader.Read());
            Assert.Equal(20261111L, reader.GetValue("H-DATE"));
            Assert.Equal(["H-DATE"], reader.Schema.Columns.Select(column => column.Name));
        }

        using (CopybookReader reader = AsciiReader(copybook, "ABC00042", new ReadOptions { RecordName = "detail" }))
        {
            Assert.True(reader.Read());
            Assert.Equal("DETAIL", reader.Record.Name);
            Assert.Equal(42L, reader.GetValue("D-QTY"));
            Assert.Null(reader.GetValue("H-DATE"));
            Assert.Equal(["D-CODE", "D-QTY"], reader.Schema.Columns.Select(column => column.Name));
            Assert.Equal("""{"D-CODE":"ABC","D-QTY":42}""", reader.ToJson());
        }

        ArgumentException error = Assert.Throws<ArgumentException>(() => AsciiReader(copybook, "x", new ReadOptions { RecordName = "TRAILER" }));
        Assert.Contains("HEAD, DETAIL", error.Message);
    }

    [Fact]
    public void A_file_is_opened_by_path_and_closed_on_dispose()
    {
        string path = Path.Combine(Path.GetTempPath(), "copybookkit-" + Guid.NewGuid().ToString("N") + ".dat");
        File.WriteAllBytes(path, Join(Alice, Bob));
        try
        {
            using (CopybookReader reader = CopybookReader.Open(path, Copybook.Parse(Customer)))
            {
                Assert.True(reader.Read());
                Assert.Equal("ALICE", reader.GetValue("CUST-NAME"));
            }

            // Deleting fails on Windows while the file is still open.
            File.Delete(path);
            Assert.False(File.Exists(path));
            Assert.Throws<FileNotFoundException>(() => CopybookReader.Open(path, Copybook.Parse(Customer)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_stream_can_be_left_open()
    {
        var stream = new MemoryStream(Alice);
        using (new CopybookReader(stream, Copybook.Parse(Customer), leaveOpen: true))
        {
        }

        Assert.True(stream.CanRead);
        using (new CopybookReader(stream, Copybook.Parse(Customer)))
        {
        }

        Assert.False(stream.CanRead);
    }

    [Fact]
    public void A_file_with_no_line_breaks_is_not_read_as_one_endless_line()
    {
        using var reader = new CopybookReader(
            new MemoryStream(new byte[(1 << 24) + 100_000]),
            Copybook.Parse("01 R.\n 05 F PIC X(4)."),
            new ReadOptions { Format = RecordFormat.LineSequential });

        RecordFormatException error = Assert.Throws<RecordFormatException>(() => reader.Read());
        Assert.Contains("Record 1 is longer than", error.Message);
        Assert.Contains("without a line break", error.Message);
    }

    /// <summary>A stream that can't report its length and returns a few bytes at a time, as a pipe can.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 7));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string ReadText(string copybook, byte[] data, Encoding encoding)
    {
        using CopybookReader reader = Reader(copybook, data, new ReadOptions { Encoding = encoding, Trim = TextTrim.None });
        Assert.True(reader.Read());
        return (string)reader.GetValue("T")!;
    }
}
