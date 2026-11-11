namespace CopybookKit.Tests;

public class ParserTests
{
    [Fact]
    public void Fields_get_the_size_and_offset_COBOL_gives_them()
    {
        Copybook copybook = Copybook.Parse("""
               01  CUSTOMER.
                   05  CUST-ID        PIC 9(8).
                   05  CUST-NAME      PIC X(30).
                   05  CUST-BALANCE   PIC S9(9)V99 COMP-3.
                   05  CUST-ORDERS    PIC S9(4) COMP.
                   05  CUST-POINTS    PIC 9(9) BINARY.
                   05  CUST-TOTAL     PIC S9(18) COMP.
            """);

        DataItem record = Assert.Single(copybook.Records);
        Assert.Equal("CUSTOMER", record.Name);
        Assert.Equal(58, record.Size);
        Assert.Equal(58, copybook.Length);

        AssertField(copybook, "CUST-ID", ItemKind.ZonedDecimal, offset: 0, size: 8);
        AssertField(copybook, "CUST-NAME", ItemKind.Text, offset: 8, size: 30);
        AssertField(copybook, "CUST-BALANCE", ItemKind.PackedDecimal, offset: 38, size: 6);
        AssertField(copybook, "CUST-ORDERS", ItemKind.Binary, offset: 44, size: 2);
        AssertField(copybook, "CUST-POINTS", ItemKind.Binary, offset: 46, size: 4);
        AssertField(copybook, "CUST-TOTAL", ItemKind.Binary, offset: 50, size: 8);

        DataItem balance = copybook.Find("cust-balance");
        Assert.Equal(11, balance.Digits);
        Assert.Equal(2, balance.Scale);
        Assert.True(balance.IsSigned);
        Assert.Equal(typeof(decimal), balance.ClrType);
        Assert.Equal(typeof(long), copybook.Find("CUST-ID").ClrType);
        Assert.Equal(typeof(string), copybook.Find("CUST-NAME").ClrType);
        Assert.Null(record.ClrType);
    }

    [Theory]
    [InlineData("X", 1)]
    [InlineData("XX", 2)]
    [InlineData("X(10)", 10)]
    [InlineData("A(3)", 3)]
    [InlineData("x(4)", 4)]
    [InlineData("-9(18)", 19)]
    [InlineData("ZZ,ZZ9.99", 9)]
    [InlineData("$$$,$$9.99CR", 12)]
    [InlineData("9(3).99", 6)]
    [InlineData("ZZ9V99", 5)]
    [InlineData("99/99/9999", 10)]
    [InlineData("+9(5)", 6)]
    [InlineData("XBX", 3)]
    public void Alphanumeric_and_edited_pictures_are_text_of_one_byte_per_position(string picture, int size)
    {
        DataItem field = Copybook.Parse($"01 R.\n 05 F PIC {picture}.").Find("F");
        Assert.Equal(ItemKind.Text, field.Kind);
        Assert.Equal(size, field.Size);
        Assert.Equal(picture, field.Picture);
    }

    [Theory]
    [InlineData("9", 1, 0, false)]
    [InlineData("9(5)", 5, 0, false)]
    [InlineData("S9(5)", 5, 0, true)]
    [InlineData("9(5)V99", 7, 2, false)]
    [InlineData("S9(4)V9(2)", 6, 2, true)]
    [InlineData("S999V9", 4, 1, true)]
    [InlineData("V99", 2, 2, false)]
    [InlineData("SV9(4)", 4, 4, true)]
    [InlineData("99PPP", 2, -3, false)]
    [InlineData("PPP99", 2, 5, false)]
    [InlineData("VPP9", 1, 3, false)]
    [InlineData("s9(3)v99", 5, 2, true)]
    public void Numeric_pictures_give_digits_scale_and_sign(string picture, int digits, int scale, bool signed)
    {
        DataItem field = Copybook.Parse($"01 R.\n 05 F PIC {picture}.").Find("F");
        Assert.Equal(ItemKind.ZonedDecimal, field.Kind);
        Assert.Equal(digits, field.Digits);
        Assert.Equal(scale, field.Scale);
        Assert.Equal(signed, field.IsSigned);
        Assert.Equal(digits, field.Size);
    }

    [Theory]
    [InlineData("PIC 9(4) COMP", 2)]
    [InlineData("PIC 9(5) COMP", 4)]
    [InlineData("PIC 9(9) COMP-4", 4)]
    [InlineData("PIC 9(10) COMPUTATIONAL", 8)]
    [InlineData("PIC S9(18) USAGE IS BINARY", 8)]
    [InlineData("PIC S9(4) COMP-5", 2)]
    [InlineData("PIC 9 COMP-3", 1)]
    [InlineData("PIC 9(2) COMP-3", 2)]
    [InlineData("PIC S9(7) PACKED-DECIMAL", 4)]
    [InlineData("PIC S9(15)V99 USAGE COMPUTATIONAL-3", 9)]
    [InlineData("COMP-1", 4)]
    [InlineData("USAGE COMP-2", 8)]
    [InlineData("USAGE IS INDEX", 4)]
    [InlineData("PIC S9(5) SIGN IS LEADING SEPARATE CHARACTER", 6)]
    [InlineData("PIC S9(5) SIGN TRAILING SEPARATE", 6)]
    [InlineData("PIC S9(5) SIGN LEADING", 5)]
    [InlineData("PIC 9(5) DISPLAY", 5)]
    public void Usage_and_sign_clauses_decide_the_size(string clauses, int size)
    {
        Assert.Equal(size, Copybook.Parse($"01 R.\n 05 F {clauses}.").Find("F").Size);
    }

    [Fact]
    public void Sign_clauses_set_where_the_sign_is()
    {
        Copybook copybook = Copybook.Parse("""
            01 R.
               05 A PIC 9(3).
               05 B PIC S9(3).
               05 C PIC S9(3) SIGN LEADING.
               05 D PIC S9(3) SIGN IS TRAILING SEPARATE.
               05 E PIC S9(3) LEADING SEPARATE.
            """);

        Assert.Equal(SignPosition.None, copybook.Find("A").Sign);
        Assert.Equal(SignPosition.TrailingEmbedded, copybook.Find("B").Sign);
        Assert.Equal(SignPosition.LeadingEmbedded, copybook.Find("C").Sign);
        Assert.Equal(SignPosition.TrailingSeparate, copybook.Find("D").Sign);
        Assert.Equal(SignPosition.LeadingSeparate, copybook.Find("E").Sign);
    }

    [Fact]
    public void A_group_is_as_long_as_its_fields_and_nests()
    {
        Copybook copybook = Copybook.Parse("""
            01 R.
               05 HEAD PIC X(2).
               05 ADDRESS.
                  10 STREET PIC X(20).
                  10 PLACE.
                     15 CITY PIC X(10).
                     15 ZIP  PIC 9(5).
               05 TAIL PIC X.
            """);

        DataItem address = copybook.Find("ADDRESS");
        Assert.True(address.IsGroup);
        Assert.Equal(2, address.Offset);
        Assert.Equal(35, address.Size);
        Assert.Equal(22, copybook.Find("PLACE").Offset);
        Assert.Equal(32, copybook.Find("ZIP").Offset);
        Assert.Equal(37, copybook.Find("TAIL").Offset);
        Assert.Equal("R.ADDRESS.PLACE.ZIP", copybook.Find("ZIP").Path);
        Assert.Same(address, copybook.Find("STREET").Parent);
        Assert.Equal(["STREET", "PLACE"], address.Children.Select(child => child.Name));
    }

    [Fact]
    public void Occurs_multiplies_the_space_an_item_takes()
    {
        Copybook copybook = Copybook.Parse("""
            01 R.
               05 MONTHS OCCURS 12 TIMES.
                  10 SALES PIC S9(7)V99 COMP-3.
                  10 DAYS  PIC 99 OCCURS 3.
               05 AFTER-IT PIC X.
            """);

        DataItem months = copybook.Find("MONTHS");
        Assert.True(months.HasOccurs);
        Assert.Equal(12, months.MinOccurs);
        Assert.Equal(12, months.MaxOccurs);
        Assert.Equal(11, months.Size);
        Assert.Equal(5, copybook.Find("DAYS").Offset);
        Assert.Equal(132, copybook.Find("AFTER-IT").Offset);
        Assert.Equal(133, copybook.Length);
        Assert.False(copybook.Find("AFTER-IT").IsVariablyLocated);
    }

    [Fact]
    public void Redefines_shares_the_storage_of_the_item_it_names()
    {
        Copybook copybook = Copybook.Parse("""
            01 R.
               05 KIND      PIC X.
               05 AS-TEXT   PIC X(10).
               05 AS-NUMBER REDEFINES AS-TEXT PIC 9(10).
               05 AS-PARTS  REDEFINES AS-NUMBER.
                  10 PART-A PIC X(4).
                  10 PART-B PIC X(8).
               05 LAST-ONE  PIC X.
            """);

        DataItem text = copybook.Find("AS-TEXT");
        DataItem number = copybook.Find("AS-NUMBER");
        DataItem parts = copybook.Find("AS-PARTS");
        Assert.Null(text.Redefines);
        Assert.Same(text, number.Redefines);
        Assert.Same(text, parts.Redefines);
        Assert.Equal(1, number.Offset);
        Assert.Equal(1, parts.Offset);
        Assert.Equal(5, copybook.Find("PART-B").Offset);
        Assert.Equal([text, number, parts], text.Alternatives);
        Assert.Same(text.Alternatives, parts.Alternatives);
        Assert.Null(copybook.Find("KIND").Alternatives);

        // The longest alternative decides where the next item starts.
        Assert.Equal(13, copybook.Find("LAST-ONE").Offset);
        Assert.Equal(14, copybook.Length);
    }

    [Fact]
    public void Occurs_depending_on_records_its_range_and_counter()
    {
        Copybook copybook = Copybook.Parse("""
            01 R.
               05 LINE-COUNT PIC 9(2).
               05 LINES OCCURS 0 TO 5 TIMES DEPENDING ON LINE-COUNT
                        ASCENDING KEY IS LINE-NO INDEXED BY LINE-IX.
                  10 LINE-NO   PIC 9(3).
                  10 LINE-TEXT PIC X(10).
               05 TRAILER PIC X(4).
            """);

        DataItem lines = copybook.Find("LINES");
        Assert.Equal(0, lines.MinOccurs);
        Assert.Equal(5, lines.MaxOccurs);
        Assert.Same(copybook.Find("LINE-COUNT"), lines.DependingOn);
        Assert.False(lines.IsVariablyLocated);
        Assert.False(copybook.Find("LINE-TEXT").IsVariablyLocated);

        DataItem trailer = copybook.Find("TRAILER");
        Assert.True(trailer.IsVariablyLocated);
        Assert.Equal(67, trailer.Offset);
        Assert.Equal(71, copybook.Length);
    }

    [Fact]
    public void A_group_passes_its_usage_and_sign_to_its_numeric_fields()
    {
        Copybook copybook = Copybook.Parse("""
            01 R.
               05 PACKED-ONES USAGE COMP-3.
                  10 P1 PIC S9(5).
                  10 P2 PIC X(3).
                  10 P3 PIC 9(4) COMP.
               05 SIGNED-ONES SIGN IS LEADING SEPARATE.
                  10 S1 PIC S9(3).
                  10 S2 PIC 9(3).
            """);

        Assert.Equal(ItemKind.PackedDecimal, copybook.Find("P1").Kind);
        Assert.Equal(ItemKind.Text, copybook.Find("P2").Kind);
        Assert.Equal(ItemKind.Binary, copybook.Find("P3").Kind);
        Assert.Equal(SignPosition.LeadingSeparate, copybook.Find("S1").Sign);
        Assert.Equal(4, copybook.Find("S1").Size);
        Assert.Equal(SignPosition.None, copybook.Find("S2").Sign);
    }

    [Fact]
    public void Clauses_that_do_not_change_storage_are_skipped()
    {
        Copybook copybook = Copybook.Parse("""
            01 R.
               05 STATUS-CODE PIC X VALUE 'A'.
                  88 IS-ACTIVE   VALUE 'A'.
                  88 IS-CLOSED   VALUES ARE 'C' THRU 'F', 'X'.
               05 TITLE PIC X(12) VALUE IS "MR. PIC X. " JUSTIFIED RIGHT.
               05 AMOUNT PIC S9(5)V99 COMP-3 VALUE -1.5 BLANK WHEN ZERO.
               05 FLAGS PIC X(2) VALUE X'00FF' EXTERNAL.
               05 FILLER PIC X(3) VALUE ALL '*'.
               66 SHORT-NAME RENAMES STATUS-CODE THRU TITLE.
            """);

        Assert.Equal(["R", "STATUS-CODE", "TITLE", "AMOUNT", "FLAGS", "FILLER"], copybook.Items.Select(item => item.Name));
        Assert.Equal(22, copybook.Length);
        Assert.True(copybook.Items[^1].IsFiller);
    }

    [Fact]
    public void Fixed_format_ignores_sequence_numbers_comments_and_columns_73_to_80()
    {
        string source = string.Join('\n',
            FixedLine("000100", '*', " A COMMENT WITH 05 X PIC X(99).", "CHG00001"),
            FixedLine("000200", ' ', "01  REC-CLIENT.", "00039300"),
            FixedLine("000300", ' ', "    03  CLIENT-ID      PIC  9(009) COMP.", "00039500"),
            FixedLine("000400", '/', "    PAGE EJECT COMMENT"),
            FixedLine("000500", 'D', "    03  DEBUG-ONLY     PIC X(50)."),
            FixedLine("000600", ' ', "    03  CLIENT-NAME    PIC  X(030).", "PIC X(9)"),
            FixedLine("000700", ' ', "    EJECT"),
            FixedLine("000800", ' ', "    03  FILLER         PIC  X(004)"),
            FixedLine("000900", ' ', "                       VALUE SPACES."));

        Copybook copybook = Copybook.Parse(source);

        Assert.Equal(["REC-CLIENT", "CLIENT-ID", "CLIENT-NAME", "FILLER"], copybook.Items.Select(item => item.Name));
        Assert.Equal(38, copybook.Length);
        Assert.Equal(3, copybook.Find("CLIENT-ID").Line);
    }

    [Fact]
    public void A_continued_literal_does_not_end_the_statement_early()
    {
        string source = string.Join('\n',
            "       01  R.",
            "           05  A PIC X(60) VALUE 'THIS LITERAL. RUNS PAST COLUMN 72 AND IS  ",
            "      -    'CONTINUED. HERE'.",
            "           05  B PIC X(2).");

        Copybook copybook = Copybook.Parse(source);

        Assert.Equal(["R", "A", "B"], copybook.Items.Select(item => item.Name));
        Assert.Equal(60, copybook.Find("B").Offset);
    }

    [Fact]
    public void Free_format_is_detected_and_allows_inline_comments()
    {
        Copybook copybook = Copybook.Parse("""
            *> customer record
            01 customer.
              05 cust-id pic 9(4). *> the key
              05 cust-name pic x(10), value spaces; justified.
            * an old-style comment
              05 a-long-line pic x(5).                                                             *> past column 72
            """);

        Assert.Equal(["customer", "cust-id", "cust-name", "a-long-line"], copybook.Items.Select(item => item.Name));
        Assert.Equal(19, copybook.Length);
        Assert.Same(copybook.Find("CUST-ID"), copybook.Find("cust-id"));
    }

    [Fact]
    public void A_line_longer_than_80_columns_means_free_format_however_it_is_indented()
    {
        // Read as fixed format, the second line is cut at column 72 and silently loses its OCCURS clause.
        string source = "        01 R.\n        05 A-LONG-FIELD-NAME-THAT-PUSHES-ITS-CLAUSES-RIGHT PIC X(2)         OCCURS 5.";

        Assert.Equal(10, Copybook.Parse(source).Length);
        Assert.Equal(2, Copybook.Parse(source, SourceFormat.Fixed).Length);
    }

    [Fact]
    public void The_source_format_can_be_forced()
    {
        // In fixed format the first six columns are a sequence area, whatever they hold.
        const string source = "LEVEL  01 R.\nNUMBER    05 F PIC X(3).";

        Assert.Equal(3, Copybook.Parse(source, SourceFormat.Fixed).Length);
        Assert.Throws<CopybookException>(() => Copybook.Parse(source, SourceFormat.Free));
    }

    [Fact]
    public void A_copybook_without_an_01_level_gets_an_implicit_record()
    {
        Copybook copybook = Copybook.Parse("""
                   05  PART-NO    PIC X(8).
                   05  PART-QTY   PIC S9(5) COMP-3.
            """);

        DataItem record = Assert.Single(copybook.Records);
        Assert.Equal("RECORD", record.Name);
        Assert.Equal(11, record.Size);
        Assert.Same(record, copybook.Find("PART-QTY").Parent);
    }

    [Fact]
    public void Several_01_levels_are_separate_records_that_are_alternatives()
    {
        Copybook copybook = Copybook.Parse("""
            01 HEADER-REC.
               05 H-TYPE PIC X.
               05 H-DATE PIC 9(8).
            01 DETAIL-REC.
               05 D-TYPE PIC X.
               05 D-AMOUNT PIC S9(7)V99 COMP-3.
               05 D-TEXT PIC X(20).
            77 STANDALONE PIC X(3).
            """);

        Assert.Equal(["HEADER-REC", "DETAIL-REC", "STANDALONE"], copybook.Records.Select(record => record.Name));
        Assert.Equal(26, copybook.Length);
        Assert.Equal(0, copybook.Find("D-TYPE").Offset);
        Assert.Equal(copybook.Records, copybook.Records[1].Alternatives);
    }

    [Fact]
    public void Names_are_found_by_qualification_when_they_repeat()
    {
        Copybook copybook = Copybook.Parse("""
            01 R.
               05 HOME.
                  10 CITY PIC X(10).
               05 WORK.
                  10 SITE.
                     15 CITY PIC X(10).
            """);

        Assert.Equal(0, copybook.Find("HOME.CITY").Offset);
        Assert.Equal(10, copybook.Find("WORK.CITY").Offset);
        Assert.Equal(10, copybook.Find("r.work.site.city").Offset);
        Assert.Throws<ArgumentException>(() => copybook.Find("CITY"));
        Assert.Throws<ArgumentException>(() => copybook.Find("R.CITY"));
        Assert.Throws<KeyNotFoundException>(() => copybook.Find("TOWN"));
        Assert.Throws<KeyNotFoundException>(() => copybook.Find("SITE.HOME.CITY"));
        Assert.False(copybook.TryFind("CITY", out _));
        Assert.True(copybook.TryFind("SITE.CITY", out DataItem? city));
        Assert.Equal(10, city!.Offset);
    }

    [Fact]
    public void Whole_numbers_of_up_to_18_digits_are_long_and_the_rest_decimal()
    {
        Copybook copybook = Copybook.Parse("""
            01 R.
               05 A PIC 9(18).
               05 B PIC 9(19).
               05 C PIC S9(3)V9.
               05 D PIC 99PPP.
               05 E PIC 9(18) COMP-5.
               05 F PIC S9(18) COMP-5.
               05 G COMP-2.
               05 H PIC S9(28) COMP-3.
            """);

        Assert.Equal(typeof(long), copybook.Find("A").ClrType);
        Assert.Equal(typeof(decimal), copybook.Find("B").ClrType);
        Assert.Equal(typeof(decimal), copybook.Find("C").ClrType);
        Assert.Equal(typeof(long), copybook.Find("D").ClrType);
        Assert.Equal(typeof(decimal), copybook.Find("E").ClrType);
        Assert.Equal(typeof(long), copybook.Find("F").ClrType);
        Assert.Equal(typeof(double), copybook.Find("G").ClrType);
        Assert.Equal(15, copybook.Find("H").Size);
    }

    [Fact]
    public void Tabs_and_a_missing_last_period_are_accepted()
    {
        Copybook copybook = Copybook.Parse("01\tR.\n\t05\tA\tPIC X(3).\n\t05\tB\tPIC 9(2)");

        Assert.Equal(5, copybook.Length);
    }

    [Theory]
    [InlineData("01 R.\n 05 F PIC 9(4) COMP SYNC.", 2, "SYNCHRONIZED")]
    [InlineData("01 R.\n 05 F PIC 9(4) COMP SYNCHRONIZED RIGHT.", 2, "SYNCHRONIZED")]
    [InlineData("01 R.\n 05 F USAGE POINTER.", 2, "POINTER")]
    [InlineData("01 R.\n 05 F PIC N(4) USAGE NATIONAL.", 2, "NATIONAL")]
    [InlineData("01 R.\n 05 F PIC N(4).", 2, "national")]
    [InlineData("01 R.\n 05 F PIC G(4) DISPLAY-1.", 2, "DISPLAY-1")]
    [InlineData("01 R.\n 05 F PIC X(4) COMP-6.", 2, "COMP-6")]
    [InlineData("01 R.\n 05 F PIC X(4).\n   10 G PIC X.", 3, "subordinate")]
    [InlineData("01 R.\n 05 F.", 2, "neither a PICTURE")]
    [InlineData("01 R.\n 05 A PIC X.\n 05 B REDEFINES Z PIC X.", 3, "redefines Z")]
    [InlineData("01 R.\n 05 A PIC X.\n 05 G.\n  10 B REDEFINES A PIC X.", 4, "redefines A")]
    [InlineData("01 R.\n 05 T OCCURS 2.\n  10 A PIC X.\n 05 U REDEFINES T PIC X(2).", 4, "OCCURS")]
    [InlineData("01 R.\n 05 T PIC X OCCURS 1 TO 5 DEPENDING ON N.", 2, "not defined before")]
    [InlineData("01 R.\n 05 T PIC X OCCURS 1 TO 5 DEPENDING ON N.\n 05 N PIC 9.", 2, "not defined before")]
    [InlineData("01 R.\n 05 N PIC X.\n 05 T PIC X OCCURS 1 TO 5 DEPENDING ON N.", 3, "not an integer")]
    [InlineData("01 R.\n 05 N PIC 9V9.\n 05 T PIC X OCCURS 1 TO 5 DEPENDING ON N.", 3, "not an integer")]
    [InlineData("01 R.\n 05 G OCCURS 2.\n  10 N PIC 9.\n 05 T PIC X OCCURS 1 TO 5 DEPENDING ON N.", 4, "inside a table")]
    [InlineData("01 R.\n 05 N PIC 9.\n 05 T PIC X DEPENDING ON N.", 3, "without OCCURS")]
    [InlineData("01 R OCCURS 3.\n 05 A PIC X.", 1, "record")]
    [InlineData("01 R.\n 05 T PIC X OCCURS UNBOUNDED.", 2, "UNBOUNDED")]
    [InlineData("01 R.\n 05 T PIC X OCCURS 5 TO 2.", 2, "not valid")]
    [InlineData("01 R.\n 05 T PIC X OCCURS 0.", 2, "not valid")]
    [InlineData("01 R.\n 05 T PIC X OCCURS MANY.", 2, "number of occurrences")]
    [InlineData("01 R.\n 50 F PIC X.", 2, "Level 50")]
    [InlineData("01 R.\n AB F PIC X.", 2, "level number")]
    [InlineData("01 R.\n COPY OTHERBOOK.", 2, "COPY")]
    [InlineData("01 R.\n EXEC SQL INCLUDE X END-EXEC.", 2, "EXEC")]
    [InlineData("01 R.\n 05 F PIC 9(29).", 2, "28 digits")]
    [InlineData("01 R.\n 05 F PIC 9(20)P(9).", 2, "28 digits")]
    [InlineData("01 R.\n 05 F PIC P(20)9(9).", 2, "28 digits")]
    [InlineData("01 R.\n 05 F PIC 9(19) COMP.", 2, "18 digits")]
    [InlineData("01 R.\n 05 F PIC 9(4) COMP-1.", 2, "USAGE doesn't allow")]
    [InlineData("01 R.\n 05 F PIC X(4) COMP-3.", 2, "not numeric")]
    [InlineData("01 R.\n 05 F PIC X(4) SIGN LEADING.", 2, "not numeric")]
    [InlineData("01 R.\n 05 F PIC X VALUE 'OPEN.", 2, "closing quote")]
    [InlineData("01 R.\n 05 F PIC X WIBBLE.", 2, "WIBBLE")]
    [InlineData("01 R.\n 05 F PIC.", 2, "picture string")]
    [InlineData("01 R.\n 05 F PIC 9V9V9.", 2, "more than one V")]
    [InlineData("01 R.\n 05 F PIC 9S9.", 2, "S somewhere")]
    [InlineData("01 R.\n 05 F PIC 9P9.", 2, "P between")]
    [InlineData("01 R.\n 05 F PIC X(0).", 2, "repeat count")]
    [InlineData("01 R.\n 05 F PIC X(A).", 2, "repeat count")]
    [InlineData("01 R.\n 05 F PIC X(4.", 2, "repeat count")]
    [InlineData("01 R.\n 05 F PIC )X.", 2, "parenthesis")]
    [InlineData("01 R.\n 05 F PIC S.", 2, "no digit")]
    [InlineData("01 R.\n 05 F PIC S9 SIGN SIDEWAYS.", 2, "LEADING or TRAILING")]
    [InlineData("01 R.\n 05 F USAGE.", 2, "a usage")]
    [InlineData("* nothing here", 0, "No data items")]
    [InlineData("", 0, "No data items")]
    public void A_copybook_that_cannot_be_laid_out_safely_is_refused(string source, int line, string message)
    {
        CopybookException error = Assert.Throws<CopybookException>(() => Copybook.Parse(source, SourceFormat.Free));

        Assert.Equal(line, error.Line);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void A_copybook_file_is_loaded_from_disk()
    {
        Copybook copybook = Copybook.Load(TestData.Sample("carddemo", "cpy", "CVACT01Y.cpy"));

        Assert.Equal("ACCOUNT-RECORD", copybook.Records[0].Name);
        Assert.Equal(300, copybook.Length);
    }

    [Fact]
    public void Describing_an_item_names_its_level_and_storage()
    {
        Assert.Equal("05 F PackedDecimal(3)", Copybook.Parse("01 R.\n 05 F PIC S9(5) COMP-3.").Find("F").ToString());
    }

    /// <summary>Builds a fixed-format line: sequence in 1-6, indicator in 7, code in 8-72, identification in 73-80.</summary>
    private static string FixedLine(string sequence, char indicator, string code, string identification = "") =>
        (sequence + indicator + code).PadRight(72) + identification;

    private static void AssertField(Copybook copybook, string name, ItemKind kind, int offset, int size)
    {
        DataItem item = copybook.Find(name);
        Assert.Equal(kind, item.Kind);
        Assert.Equal(offset, item.Offset);
        Assert.Equal(size, item.Size);
    }
}
