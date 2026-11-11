using System.Globalization;
using System.Text;
using System.Text.Json;
using static CopybookKit.Tests.TestData;

namespace CopybookKit.Tests;

public class ExportTests
{
    private const string Order = """
        01 ORDER-REC.
           05 ORDER-ID PIC 9(4).
           05 CUSTOMER.
              10 CUST-NAME PIC X(12).
              10 FILLER    PIC X(2).
           05 FILLER.
              10 PRIORITY PIC X.
           05 LINES OCCURS 2.
              10 SKU   PIC X(3).
              10 PRICE PIC S9(3)V99.
           05 FLAGS PIC X OCCURS 3.
           05 TOTAL PIC S9(5)V99 SIGN LEADING SEPARATE.
        """;

    private const string First = "0001Smith, \"Al\" ..HAAA01250BBB0009}XYZ+0012410";
    private const string Second = "000X  O'Neil    ..L   00000CCC     X Z-0000001";

    [Fact]
    public void Json_nests_groups_and_tables_and_leaves_filler_out()
    {
        using CopybookReader reader = AsciiReader(Order, First + Second);

        Assert.True(reader.Read());
        Assert.Equal(
            """{"ORDER-ID":1,"CUSTOMER":{"CUST-NAME":"Smith, \u0022Al\u0022"},"PRIORITY":"H","LINES":[{"SKU":"AAA","PRICE":12.50},{"SKU":"BBB","PRICE":-0.90}],"FLAGS":["X","Y","Z"],"TOTAL":124.10}""",
            reader.ToJson());

        Assert.True(reader.Read());
        using JsonDocument json = JsonDocument.Parse(reader.ToJson());
        JsonElement root = json.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ORDER-ID").ValueKind);
        Assert.Equal("  O'Neil", root.GetProperty("CUSTOMER").GetProperty("CUST-NAME").GetString());
        Assert.Equal(string.Empty, root.GetProperty("LINES")[0].GetProperty("SKU").GetString());
        Assert.Equal(0.00m, root.GetProperty("LINES")[0].GetProperty("PRICE").GetDecimal());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("LINES")[1].GetProperty("PRICE").ValueKind);
        Assert.Equal(["X", "", "Z"], root.GetProperty("FLAGS").EnumerateArray().Select(flag => flag.GetString()));
        Assert.Equal(-0.01m, root.GetProperty("TOTAL").GetDecimal());
    }

    [Fact]
    public void Json_lines_writes_one_object_per_record()
    {
        using CopybookReader reader = AsciiReader(Order, First + Second + First);
        using var output = new MemoryStream();

        Assert.Equal(3, reader.WriteJsonLines(output));

        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal(string.Empty, lines[3]);
        Assert.Equal(lines[0], lines[2]);
        foreach (string line in lines[..3])
        {
            using JsonDocument json = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        }

        Assert.Equal(3, reader.Statistics.Records);
        Assert.Equal(1, reader.Statistics.InvalidFields);
        Assert.Equal(1, reader.Statistics.BlankNumericFields);
    }

    [Fact]
    public void A_record_that_fails_leaves_no_partial_line_behind()
    {
        using CopybookReader reader = AsciiReader(Order, First + Second + First, new ReadOptions { InvalidData = InvalidDataPolicy.Throw });
        using var output = new MemoryStream();

        Assert.Throws<RecordFormatException>(() => reader.WriteJsonLines(output));

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.EndsWith(",\"TOTAL\":124.10}\n", text);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void A_limit_stops_early_and_leaves_the_rest_unread()
    {
        using CopybookReader reader = AsciiReader(Order, First + Second + First);
        using var output = new MemoryStream();

        Assert.Equal(2, reader.WriteJsonLines(output, limit: 2));
        Assert.Equal(2, reader.RecordNumber);
        Assert.True(reader.Read());
        Assert.Equal(3, reader.RecordNumber);

        using CopybookReader none = AsciiReader(Order, First);
        Assert.Equal(0, none.WriteCsv(new StringWriter(), limit: 0));
        Assert.Equal(0, none.RecordNumber);
    }

    [Fact]
    public void Csv_has_a_header_quotes_what_needs_it_and_leaves_nulls_empty()
    {
        using CopybookReader reader = AsciiReader(Order, First + Second);
        var output = new StringWriter();

        Assert.Equal(2, reader.WriteCsv(output));

        Assert.Equal(
            "ORDER-ID,CUST-NAME,PRIORITY,SKU(1),PRICE(1),SKU(2),PRICE(2),FLAGS(1),FLAGS(2),FLAGS(3),TOTAL\r\n" +
            "1,\"Smith, \"\"Al\"\"\",H,AAA,12.50,BBB,-0.90,X,Y,Z,124.10\r\n" +
            ",\"  O'Neil\",L,,0.00,CCC,,X,,Z,-0.01\r\n",
            output.ToString());
    }

    [Fact]
    public void Csv_numbers_do_not_follow_the_current_culture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            using CopybookReader reader = Reader("01 R.\n 05 A PIC S9(3)V99 COMP-3.\n 05 B COMP-2.", Hex("12345D 4118000000000000"));
            var output = new StringWriter();

            reader.WriteCsv(output);

            Assert.Equal("A,B\r\n-123.45,1.5\r\n", output.ToString());
            Assert.True(reader.Read() is false);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Json_can_be_written_into_a_larger_document()
    {
        using CopybookReader reader = AsciiReader("01 R.\n 05 A PIC X(2).", "ABCD");
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            while (reader.Read())
            {
                reader.WriteJson(writer);
            }

            writer.WriteEndArray();
        }

        Assert.Equal("""[{"A":"AB"},{"A":"CD"}]""", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void A_record_that_is_a_single_field_still_becomes_an_object()
    {
        using CopybookReader reader = AsciiReader("01 WHOLE-LINE PIC X(5).", "hello");
        Assert.True(reader.Read());

        Assert.Equal("""{"WHOLE-LINE":"hello"}""", reader.ToJson());
        Assert.Equal(["hello"], reader.GetValues());
    }

    [Fact]
    public void A_filler_table_keeps_its_place_in_json()
    {
        const string copybook = """
            01 R.
               05 FILLER OCCURS 2.
                  10 CODE PIC X.
               05 FILLER PIC X OCCURS 2.
               05 LAST-ONE PIC X.
            """;
        using CopybookReader reader = AsciiReader(copybook, "AB--Z");
        Assert.True(reader.Read());

        Assert.Equal("""{"FILLER":[{"CODE":"A"},{"CODE":"B"}],"LAST-ONE":"Z"}""", reader.ToJson());
        Assert.Equal(["CODE(1)", "CODE(2)", "LAST-ONE"], reader.Schema.Columns.Select(column => column.Name));
        Assert.Equal(["A", "B", "Z"], reader.GetValues());
    }
}
