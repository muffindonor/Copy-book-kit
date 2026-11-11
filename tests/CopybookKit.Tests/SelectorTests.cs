using static CopybookKit.Tests.TestData;

namespace CopybookKit.Tests;

public class SelectorTests
{
    private const string Mixed = """
        01 R.
           05 REC-TYPE PIC X.
           05 BODY PIC X(8).
           05 AS-PAYMENT REDEFINES BODY.
              10 PAY-AMOUNT PIC S9(5)V99.
              10 PAY-FLAG   PIC X.
           05 AS-NOTE REDEFINES BODY.
              10 NOTE-TEXT PIC X(8).
           05 SEQ PIC 9(2).
        """;

    private static ReadOptions ByType() => new()
    {
        Selectors =
        {
            new RedefineRule("REC-TYPE", new Dictionary<string, string> { ["P"] = "AS-PAYMENT", ["N"] = "AS-NOTE", ["B"] = "BODY" }),
        },
    };

    [Fact]
    public void Without_a_rule_every_alternative_is_decoded()
    {
        using CopybookReader reader = AsciiReader(Mixed, "P0001250Y01Nhello   02");

        Assert.True(reader.Read());
        Assert.Equal(12.50m, reader.GetValue("PAY-AMOUNT"));
        Assert.Equal("0001250Y", reader.GetValue("NOTE-TEXT"));
        Assert.Equal("0001250Y", reader.GetValue("BODY"));

        Assert.True(reader.Read());
        Assert.Null(reader.GetValue("PAY-AMOUNT"));
        Assert.Equal("hello", reader.GetValue("NOTE-TEXT"));
        Assert.Equal(2L, reader.GetValue("SEQ"));
        Assert.Equal(1, reader.Statistics.InvalidFields);
    }

    [Fact]
    public void A_rule_decodes_only_the_alternative_the_record_type_names()
    {
        using CopybookReader reader = AsciiReader(Mixed, "P0001250Y01Nhello   02BrawBody 03", ByType());

        Assert.True(reader.Read());
        Assert.Equal("""{"REC-TYPE":"P","AS-PAYMENT":{"PAY-AMOUNT":12.50,"PAY-FLAG":"Y"},"SEQ":1}""", reader.ToJson());
        Assert.Null(reader.GetValue("NOTE-TEXT"));
        Assert.Null(reader.GetValue("BODY"));

        Assert.True(reader.Read());
        Assert.Equal("""{"REC-TYPE":"N","AS-NOTE":{"NOTE-TEXT":"hello"},"SEQ":2}""", reader.ToJson());
        Assert.Null(reader.GetValue("PAY-AMOUNT"));

        // When the redefined item itself is ruled out, the fields after it still start in the right place.
        Assert.True(reader.Read());
        Assert.Equal("""{"REC-TYPE":"B","BODY":"rawBody","SEQ":3}""", reader.ToJson());

        Assert.Equal(0, reader.Statistics.InvalidFields);
        Assert.Equal(0, reader.Statistics.UnmatchedRecords);
        Assert.Equal(
            ["REC-TYPE", "BODY", "PAY-AMOUNT", "PAY-FLAG", "NOTE-TEXT", "SEQ"],
            reader.Schema.Columns.Select(column => column.Name));
    }

    [Fact]
    public void A_record_type_no_rule_lists_is_decoded_every_way_and_counted()
    {
        using CopybookReader reader = AsciiReader(Mixed, "Nhello   01?0001250Y02Nhello   03", ByType());

        Assert.True(reader.Read());
        Assert.Null(reader.GetValue("PAY-AMOUNT"));
        Assert.Equal(0, reader.Statistics.UnmatchedRecords);

        // The choice made for the record before must not carry over to this one.
        Assert.True(reader.Read());
        Assert.Equal(12.50m, reader.GetValue("PAY-AMOUNT"));
        Assert.Equal("0001250Y", reader.GetValue("NOTE-TEXT"));
        Assert.Equal("0001250Y", reader.GetValue("BODY"));
        Assert.Equal(1, reader.Statistics.UnmatchedRecords);

        Assert.True(reader.Read());
        Assert.Null(reader.GetValue("PAY-AMOUNT"));
        Assert.Equal("hello", reader.GetValue("NOTE-TEXT"));
        Assert.Equal(1, reader.Statistics.UnmatchedRecords);
    }

    [Fact]
    public void A_chosen_alternative_shorter_than_the_item_it_redefines_does_not_move_later_fields()
    {
        const string copybook = """
            01 R.
               05 REC-TYPE PIC X.
               05 BODY PIC X(8).
               05 AS-CODE REDEFINES BODY.
                  10 CODE PIC X(3).
               05 SEQ PIC 9(2).
            """;
        var options = new ReadOptions { Selectors = { new RedefineRule("REC-TYPE", new Dictionary<string, string> { ["C"] = "AS-CODE" }) } };
        using CopybookReader reader = AsciiReader(copybook, "CABC4567807", options);
        Assert.True(reader.Read());

        Assert.Equal("""{"REC-TYPE":"C","AS-CODE":{"CODE":"ABC"},"SEQ":7}""", reader.ToJson());
        Assert.Equal(["C", null, "ABC", 7L], reader.GetValues());
    }

    [Fact]
    public void A_numeric_selector_field_is_matched_by_its_value()
    {
        const string copybook = """
            01 R.
               05 KEY-PART.
                  10 KIND PIC 9(4) COMP.
               05 MAIN-PART.
                  10 NAME PIC X(6).
               05 OTHER-PART REDEFINES MAIN-PART.
                  10 QTY PIC S9(7) COMP-3.
                  10 FILLER PIC X(2).
            """;
        var options = new ReadOptions
        {
            Selectors = { new RedefineRule("KIND", new Dictionary<string, string> { ["1"] = "MAIN-PART", [" 2 "] = "OTHER-PART" }) },
        };
        using CopybookReader reader = Reader(copybook, Join(Hex("0001"), Ebcdic("WIDGET"), Hex("0002"), Hex("0001234D"), Ebcdic("  ")), options);

        Assert.True(reader.Read());
        Assert.Equal("""{"KEY-PART":{"KIND":1},"MAIN-PART":{"NAME":"WIDGET"}}""", reader.ToJson());
        Assert.True(reader.Read());
        Assert.Equal("""{"KEY-PART":{"KIND":2},"OTHER-PART":{"QTY":-1234}}""", reader.ToJson());
    }

    [Fact]
    public void A_rule_can_switch_between_whole_records()
    {
        const string copybook = """
            01 HEADER.
               05 H-TYPE PIC X.
               05 H-DATE PIC 9(8).
            01 DETAIL.
               05 D-TYPE PIC X.
               05 D-CODE PIC X(3).
               05 D-QTY  PIC 9(3).
            01 UNUSED.
               05 U-TEXT PIC X(4).
            """;
        var options = new ReadOptions
        {
            Format = RecordFormat.LineSequential,
            Selectors = { new RedefineRule("H-TYPE", new Dictionary<string, string> { ["H"] = "HEADER", ["D"] = "DETAIL" }) },
        };
        using CopybookReader reader = AsciiReader(copybook, "H20261111\nDABC007\nDXYZ\n", options);

        Assert.Equal(["H-TYPE", "H-DATE", "D-TYPE", "D-CODE", "D-QTY"], reader.Schema.Columns.Select(column => column.Name));

        Assert.True(reader.Read());
        Assert.Equal("HEADER", reader.Record.Name);
        Assert.Equal(["H", 20261111L, null, null, null], reader.GetValues());

        Assert.True(reader.Read());
        Assert.Equal("DETAIL", reader.Record.Name);
        Assert.Equal([null, null, "D", "ABC", 7L], reader.GetValues());
        Assert.Equal("""{"D-TYPE":"D","D-CODE":"ABC","D-QTY":7}""", reader.ToJson());

        // A short line is padded with spaces, so the quantity is blank, not missing.
        Assert.True(reader.Read());
        Assert.Equal([null, null, "D", "XYZ", null], reader.GetValues());
        Assert.Equal(1, reader.Statistics.BlankNumericFields);
        Assert.Equal(0, reader.Statistics.MissingFields);
    }

    [Fact]
    public void Columns_that_share_a_name_are_qualified()
    {
        const string copybook = """
            01 R.
               05 HOME.
                  10 CITY PIC X(3).
               05 WORK.
                  10 CITY PIC X(3).
               05 FILLER.
                  10 ZIP PIC X(2).
            """;
        using CopybookReader reader = AsciiReader(copybook, "TLVNYC99");
        Assert.True(reader.Read());

        Assert.Equal(["HOME.CITY", "WORK.CITY", "ZIP"], reader.Schema.Columns.Select(column => column.Name));
        Assert.Equal(["TLV", "NYC", "99"], reader.GetValues());
        Assert.Equal("""{"HOME":{"CITY":"TLV"},"WORK":{"CITY":"NYC"},"ZIP":"99"}""", reader.ToJson());
    }

    [Theory]
    [InlineData("NO-SUCH", "P", "AS-PAYMENT", "no item named NO-SUCH")]
    [InlineData("REC-TYPE", "P", "NO-SUCH", "no item named NO-SUCH")]
    [InlineData("REC-TYPE", "P", "SEQ", "has no alternatives")]
    [InlineData("REC-TYPE", "P", "PAY-AMOUNT", "has no alternatives")]
    [InlineData("AS-NOTE", "P", "AS-PAYMENT", "must be a text or decimal field")]
    public void A_rule_that_cannot_work_is_refused_when_the_reader_is_created(string field, string value, string target, string message)
    {
        var options = new ReadOptions { Selectors = { new RedefineRule(field, new Dictionary<string, string> { [value] = target }) } };

        ArgumentException error = Assert.Throws<ArgumentException>(() => AsciiReader(Mixed, "x", options));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("ITEM-CODE")]
    [InlineData("TRAILER")]
    public void A_selector_field_must_have_a_fixed_position(string field)
    {
        const string copybook = """
            01 R.
               05 ITEM-COUNT PIC 9.
               05 ITEMS OCCURS 0 TO 3 DEPENDING ON ITEM-COUNT.
                  10 ITEM-CODE PIC X(2).
               05 TRAILER PIC X(3).
               05 ALT REDEFINES TRAILER PIC 9(3).
            """;
        var options = new ReadOptions { Selectors = { new RedefineRule(field, new Dictionary<string, string> { ["A"] = "ALT" }) } };

        ArgumentException error = Assert.Throws<ArgumentException>(() => AsciiReader(copybook, "x", options));
        Assert.Contains("fixed position", error.Message);
    }
}
