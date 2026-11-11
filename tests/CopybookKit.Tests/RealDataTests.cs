using System.Globalization;
using System.Text;

namespace CopybookKit.Tests;

/// <summary>
/// Reads EBCDIC files published by other projects and checks the result against an
/// independent source: the same data in ASCII, another tool's output, or a second file
/// that stores the same values in a different format. Samples/NOTICE.md lists the sources.
/// </summary>
public class RealDataTests
{
    private const string ExportSelector = "EXPORT-REC-TYPE";

    private static readonly Dictionary<string, string> ExportCases = new()
    {
        ["C"] = "EXPORT-CUSTOMER-DATA",
        ["A"] = "EXPORT-ACCOUNT-DATA",
        ["T"] = "EXPORT-TRANSACTION-DATA",
        ["X"] = "EXPORT-CARD-XREF-DATA",
        ["D"] = "EXPORT-CARD-DATA",
    };

    /// <summary>
    /// The CardDemo files exist in EBCDIC and in ASCII. Decoding both must give the same
    /// values, apart from the records where the two published data sets really differ.
    /// </summary>
    [Theory]
    [InlineData("ACCTDATA", "acctdata", "CVACT01Y", 50, 1)]
    [InlineData("CARDDATA", "carddata", "CVACT02Y", 50, 0)]
    [InlineData("CARDXREF", "cardxref", "CVACT03Y", 50, 0)]
    [InlineData("CUSTDATA", "custdata", "CVCUS01Y", 50, 0)]
    [InlineData("DALYTRAN", "dailytran", "CVTRA06Y", 300, 0)]
    [InlineData("DISCGRP", "discgrp", "CVTRA02Y", 51, 1)]
    [InlineData("TCATBALF", "tcatbal", "CVTRA01Y", 50, 0)]
    [InlineData("TRANCATG", "trancatg", "CVTRA04Y", 18, 0)]
    [InlineData("TRANTYPE", "trantype", "CVTRA03Y", 7, 0)]
    public void CardDemo_files_decode_the_same_from_ebcdic_and_ascii(string ebcdicName, string asciiName, string copybookName, int records, int knownDifferences)
    {
        Copybook copybook = CardDemoCopybook(copybookName);
        List<object?[]> fromEbcdic = ReadAll(CardDemoEbcdic(ebcdicName), copybook, new ReadOptions(), out ReadStatistics ebcdicStatistics);
        List<object?[]> fromAscii = ReadAll(
            TestData.Sample("carddemo", "ascii", asciiName + ".txt"),
            copybook,
            new ReadOptions { Encoding = Encoding.ASCII, Format = RecordFormat.LineSequential },
            out ReadStatistics asciiStatistics);

        Assert.Equal(records, fromEbcdic.Count);
        Assert.Equal(records, fromAscii.Count);
        Assert.Equal(0, ebcdicStatistics.InvalidFields + ebcdicStatistics.BlankNumericFields + ebcdicStatistics.MissingFields);
        Assert.Equal(0, asciiStatistics.InvalidFields + asciiStatistics.BlankNumericFields + asciiStatistics.MissingFields);

        int different = 0;
        for (int i = 0; i < records; i++)
        {
            if (!fromEbcdic[i].SequenceEqual(fromAscii[i]))
            {
                different++;
            }
        }

        Assert.Equal(knownDifferences, different);
    }

    [Fact]
    public void The_two_CardDemo_differences_are_in_the_published_data_not_the_decoding()
    {
        // The EBCDIC file's bytes really do say something else than the ASCII file's.
        using CopybookReader accounts = CopybookReader.Open(CardDemoEbcdic("ACCTDATA"), CardDemoCopybook("CVACT01Y"));
        for (int i = 0; i < 49; i++)
        {
            Assert.True(accounts.Read());
        }

        Assert.Equal(49L, accounts.GetValue("ACCT-ID"));
        Assert.Equal("ZEROAPR", accounts.GetValue("ACCT-ADDR-ZIP"));
        Assert.Equal("E9C5D9D6C1D7D9404040", Convert.ToHexString(accounts.RawRecord.Slice(accounts.Copybook.Find("ACCT-ADDR-ZIP").Offset, 10)));

        using CopybookReader groups = CopybookReader.Open(CardDemoEbcdic("DISCGRP"), CardDemoCopybook("CVTRA02Y"));
        for (int i = 0; i < 34; i++)
        {
            Assert.True(groups.Read());
        }

        Assert.Equal(15.00m, groups.GetValue("DIS-INT-RATE"));
        Assert.Equal("F0F0F1F5F0C0", Convert.ToHexString(groups.RawRecord.Slice(groups.Copybook.Find("DIS-INT-RATE").Offset, 6)));
    }

    [Fact]
    public void CardDemo_account_values_are_read_exactly()
    {
        using CopybookReader reader = CopybookReader.Open(CardDemoEbcdic("ACCTDATA"), CardDemoCopybook("CVACT01Y"));
        Assert.True(reader.Read());

        Assert.Equal(
            """{"ACCT-ID":1,"ACCT-ACTIVE-STATUS":"Y","ACCT-CURR-BAL":194.00,"ACCT-CREDIT-LIMIT":2020.00,"ACCT-CASH-CREDIT-LIMIT":1020.00,"ACCT-OPEN-DATE":"2014-11-20","ACCT-EXPIRAION-DATE":"2025-05-20","ACCT-REISSUE-DATE":"2025-05-20","ACCT-CURR-CYC-CREDIT":0.00,"ACCT-CURR-CYC-DEBIT":0.00,"ACCT-ADDR-ZIP":"A000000000","ACCT-GROUP-ID":""}""",
            reader.ToJson());

        decimal balance = 194.00m;
        long count = 1;
        while (reader.Read())
        {
            balance += reader.GetDecimal("ACCT-CURR-BAL")!.Value;
            count++;
            Assert.Equal(count, reader.GetInt64("ACCT-ID"));
        }

        Assert.Equal(50, count);
        Assert.True(balance > 0);
    }

    /// <summary>
    /// The export file holds the other files' data again as COMP and COMP-3 fields in five
    /// record layouts. Values decoded from binary and packed storage must equal the same
    /// values decoded from plain digits in the other files.
    /// </summary>
    [Fact]
    public void CardDemo_export_file_agrees_with_the_files_it_was_made_from()
    {
        Dictionary<string, Dictionary<string, object?>> transactions = Index("DALYTRAN", "CVTRA06Y", "DALYTRAN-ID");
        Dictionary<string, Dictionary<string, object?>> crossReferences = Index("CARDXREF", "CVACT03Y", "XREF-CARD-NUM");
        Dictionary<string, Dictionary<string, object?>> cards = Index("CARDDATA", "CVACT02Y", "CARD-NUM");
        Dictionary<string, Dictionary<string, object?>> customers = Index("CUSTDATA", "CVCUS01Y", "CUST-ID");
        Dictionary<string, Dictionary<string, object?>> accounts = Index("ACCTDATA", "CVACT01Y", "ACCT-ID");

        var options = new ReadOptions { Selectors = { new RedefineRule(ExportSelector, ExportCases) } };
        using CopybookReader export = CopybookReader.Open(CardDemoEbcdic("EXPORT.DATA"), CardDemoCopybook("CVEXPORT"), options);

        var seen = new Dictionary<string, int>();
        int customersMatching = 0;
        int accountsMatching = 0;
        while (export.Read())
        {
            string type = (string)export.GetValue("EXPORT-REC-TYPE")!;
            seen[type] = seen.GetValueOrDefault(type) + 1;
            switch (type)
            {
                case "T":
                    Dictionary<string, object?> transaction = transactions[(string)export.GetValue("EXP-TRAN-ID")!];
                    Assert.Equal(transaction["DALYTRAN-AMT"], export.GetValue("EXP-TRAN-AMT")); // COMP-3 against zoned
                    Assert.Equal(transaction["DALYTRAN-MERCHANT-ID"], export.GetValue("EXP-TRAN-MERCHANT-ID")); // COMP against zoned
                    Assert.Equal(transaction["DALYTRAN-CAT-CD"], export.GetValue("EXP-TRAN-CAT-CD"));
                    Assert.Equal(transaction["DALYTRAN-MERCHANT-NAME"], export.GetValue("EXP-TRAN-MERCHANT-NAME"));
                    Assert.Equal(transaction["DALYTRAN-CARD-NUM"], export.GetValue("EXP-TRAN-CARD-NUM"));
                    Assert.Equal(transaction["DALYTRAN-ORIG-TS"], export.GetValue("EXP-TRAN-ORIG-TS"));
                    break;

                case "X":
                    Dictionary<string, object?> crossReference = crossReferences[(string)export.GetValue("EXP-XREF-CARD-NUM")!];
                    Assert.Equal(crossReference["XREF-CUST-ID"], export.GetValue("EXP-XREF-CUST-ID"));
                    Assert.Equal(crossReference["XREF-ACCT-ID"], export.GetValue("EXP-XREF-ACCT-ID")); // 8-byte COMP against zoned
                    break;

                case "D":
                    Dictionary<string, object?> card = cards[(string)export.GetValue("EXP-CARD-NUM")!];
                    Assert.Equal(card["CARD-ACCT-ID"], export.GetValue("EXP-CARD-ACCT-ID"));
                    Assert.Equal(card["CARD-CVV-CD"], export.GetValue("EXP-CARD-CVV-CD")); // 2-byte COMP against zoned
                    Assert.Equal(card["CARD-EMBOSSED-NAME"], export.GetValue("EXP-CARD-EMBOSSED-NAME"));
                    Assert.Equal(card["CARD-EXPIRAION-DATE"], export.GetValue("EXP-CARD-EXPIRAION-DATE"));
                    break;

                case "C":
                    Dictionary<string, object?> customer = customers[export.GetString("EXP-CUST-ID")!];
                    if (Equals(customer["CUST-FIRST-NAME"], export.GetValue("EXP-CUST-FIRST-NAME"))
                        && Equals(customer["CUST-LAST-NAME"], export.GetValue("EXP-CUST-LAST-NAME"))
                        && Equals(customer["CUST-FICO-CREDIT-SCORE"], export.GetValue("EXP-CUST-FICO-CREDIT-SCORE"))
                        && Equals(customer["CUST-ADDR-LINE-1"], export.GetValue("EXP-CUST-ADDR-LINE", 1))
                        && Equals(customer["CUST-ADDR-LINE-3"], export.GetValue("EXP-CUST-ADDR-LINE", 3))
                        && Equals(customer["CUST-PHONE-NUM-2"], export.GetValue("EXP-CUST-PHONE-NUM", 2))
                        && Equals(customer["CUST-DOB-YYYY-MM-DD"], export.GetValue("EXP-CUST-DOB-YYYY-MM-DD")))
                    {
                        customersMatching++;
                    }

                    break;

                case "A":
                    Dictionary<string, object?> account = accounts[export.GetString("EXP-ACCT-ID")!];
                    if (Equals(account["ACCT-CURR-BAL"], export.GetValue("EXP-ACCT-CURR-BAL"))
                        && Equals(account["ACCT-CREDIT-LIMIT"], export.GetValue("EXP-ACCT-CREDIT-LIMIT"))
                        && Equals(account["ACCT-CASH-CREDIT-LIMIT"], export.GetValue("EXP-ACCT-CASH-CREDIT-LIMIT"))
                        && Equals(account["ACCT-CURR-CYC-DEBIT"], export.GetValue("EXP-ACCT-CURR-CYC-DEBIT"))
                        && Equals(account["ACCT-OPEN-DATE"], export.GetValue("EXP-ACCT-OPEN-DATE")))
                    {
                        accountsMatching++;
                    }

                    break;
            }
        }

        Assert.Equal(300, seen["T"]);
        Assert.Equal(50, seen["X"]);
        Assert.Equal(50, seen["D"]);
        Assert.Equal(50, seen["C"]);
        Assert.Equal(50, seen["A"]);
        Assert.Equal(500, export.Statistics.Records);

        // In two customers and two accounts, the two published files hold different values.
        Assert.Equal(48, customersMatching);
        Assert.Equal(48, accountsMatching);

        Assert.Equal(0, export.Statistics.InvalidFields);
        Assert.Equal(0, export.Statistics.MissingFields);
        Assert.Equal(0, export.Statistics.UnmatchedRecords);
    }

    [Fact]
    public void CardDemo_export_file_without_a_selector_reports_the_fields_it_cannot_read()
    {
        using CopybookReader export = CopybookReader.Open(CardDemoEbcdic("EXPORT.DATA"), CardDemoCopybook("CVEXPORT"));
        long written = export.WriteJsonLines(Stream.Null);

        Assert.Equal(500, written);
        Assert.True(export.Statistics.InvalidFields > 1000);
        Assert.Equal(ReadStatistics.MaxIssues, export.Statistics.Issues.Count);
    }

    /// <summary>
    /// Three record layouts in one file, picked by a binary field. The expected values are
    /// the output of the AWS Mainframe Data Utilities, a separate implementation in Python.
    /// </summary>
    [Fact]
    public void Aws_multi_layout_file_matches_the_aws_tool_output()
    {
        string[] expected = File.ReadAllLines(TestData.Sample("aws-mdu", "CLIENT.ASCII.txt"));
        var options = new ReadOptions
        {
            Selectors =
            {
                new RedefineRule("CLIENT-TYPE", new Dictionary<string, string> { ["0"] = "CLIENT-HEADER", ["1"] = "CLIENT-MAIN", ["2"] = "CLIENT-ADDRESS" }),
            },
        };
        using CopybookReader reader = CopybookReader.Open(TestData.Sample("aws-mdu", "CLIENT.EBCDIC.txt"), Copybook.Load(TestData.Sample("aws-mdu", "COBKS05.cpy")), options);

        int records = 0;
        foreach (string line in expected)
        {
            Assert.True(reader.Read());
            string[] cells = line.Split('|');
            Assert.Equal(long.Parse(cells[0], CultureInfo.InvariantCulture), reader.GetValue("CLIENT-ID"));
            long type = long.Parse(cells[1], CultureInfo.InvariantCulture);
            Assert.Equal(type, reader.GetValue("CLIENT-TYPE"));
            switch (type)
            {
                case 0:
                    Assert.Equal(long.Parse(cells[2], CultureInfo.InvariantCulture), reader.GetValue("CLIENT-RECORD-COUNT"));
                    Assert.Null(reader.GetValue("CLIENT-NAME"));
                    break;
                case 1:
                    Assert.Equal(cells[2], reader.GetValue("CLIENT-NAME"));
                    Assert.Equal(cells[3], reader.GetValue("CLIENT-BDATE"));
                    Assert.Equal(cells[4], reader.GetValue("CLIENT-ED-LVL"));
                    Assert.Equal(decimal.Parse(cells[5], CultureInfo.InvariantCulture), reader.GetValue("CLIENT-INCOME"));
                    Assert.Null(reader.GetValue("CLIENT-ADDR-STREET"));
                    break;
                default:
                    Assert.Equal(long.Parse(cells[2], CultureInfo.InvariantCulture), reader.GetValue("CLIENT-ADDR-NUMBER"));
                    Assert.Equal(cells[3], reader.GetValue("CLIENT-ADDR-STREET"));
                    Assert.Null(reader.GetValue("CLIENT-INCOME"));
                    break;
            }

            records++;
        }

        Assert.False(reader.Read());
        Assert.Equal(221, records);
        Assert.Equal(0, reader.Statistics.InvalidFields + reader.Statistics.UnmatchedRecords);
    }

    /// <summary>Variable-length records with descriptor words, holding a table whose size depends on a COMP-3 field.</summary>
    [Fact]
    public void Aws_variable_length_file_matches_the_aws_tool_output()
    {
        string[] expected = File.ReadAllLines(TestData.Sample("aws-mdu", "COBVBFM2.ASCII.txt"));
        using CopybookReader reader = CopybookReader.Open(
            TestData.Sample("aws-mdu", "COBVBFM2.EBCDIC.txt"),
            Copybook.Load(TestData.Sample("aws-mdu", "COBVBFM2.cpy")),
            new ReadOptions { Format = RecordFormat.VariableRdw });

        var lengths = new HashSet<int>();
        foreach (string line in expected)
        {
            Assert.True(reader.Read());
            lengths.Add(reader.RawRecord.Length);
            string[] cells = line.Split('|');
            Assert.Equal(cells[0], reader.GetValue("OUTK-TYPE"));
            Assert.Equal(long.Parse(cells[1], CultureInfo.InvariantCulture), reader.GetValue("OUTK-SEQT"));
            long count = long.Parse(cells[2], CultureInfo.InvariantCulture);
            Assert.Equal(count, reader.GetValue("OUT-REC-CNT"));
            Assert.Equal(6 + (30 * (int)count), reader.RawRecord.Length);

            for (int i = 1; i <= 10; i++)
            {
                if (i <= count)
                {
                    Assert.Equal(long.Parse(cells[1 + (2 * i)], CultureInfo.InvariantCulture), reader.GetValue("OUT-REC-NO", i));
                    Assert.Equal(cells[2 + (2 * i)], reader.GetValue("OUT-NAME", i));
                }
                else
                {
                    Assert.Null(reader.GetValue("OUT-REC-NO", i));
                    Assert.Null(reader.GetValue("OUT-NAME", i));
                }
            }
        }

        Assert.False(reader.Read());
        Assert.Equal(20, reader.Statistics.Records);
        Assert.Equal(10, lengths.Count);
        Assert.Equal(0, reader.Statistics.InvalidFields + reader.Statistics.MissingFields);
    }

    /// <summary>Every binary and packed size, signed and unsigned, against the AWS tool's output for 2,000 records.</summary>
    [Fact]
    public void Aws_packed_and_binary_file_matches_the_aws_tool_output()
    {
        string[] expected = File.ReadAllLines(TestData.Sample("aws-mdu", "COBPACK.ASCII.2000.txt"));
        string[] numericFields =
        [
            "OUTFILE-UNPACKED", "OUTFILE-UNPACKED-S", "OUTFILE-COMP-04", "OUTFILE-COMP-04-S", "OUTFILE-COMP-09", "OUTFILE-COMP-09-S",
            "OUTFILE-COMP-18", "OUTFILE-COMP-18-S", "OUTFILE-COMP3-04", "OUTFILE-COMP3-04-S", "OUTFILE-COMP3-09", "OUTFILE-COMP3-09-S",
            "OUTFILE-COMP3-18", "OUTFILE-COMP3-18-S",
        ];
        using CopybookReader reader = CopybookReader.Open(TestData.Sample("aws-mdu", "COBPACK.EBCDIC.2000.bin"), Copybook.Load(TestData.Sample("aws-mdu", "COBPACK2.cpy")));
        Assert.Equal(150, reader.Copybook.Length);

        bool sawNegative = false;
        foreach (string line in expected)
        {
            Assert.True(reader.Read());
            string[] cells = line.Split('|');
            Assert.Equal(cells[0], reader.GetValue("OUTFILE-TEXT"));
            for (int i = 0; i < numericFields.Length; i++)
            {
                long value = long.Parse(cells[i + 1], CultureInfo.InvariantCulture);
                sawNegative |= value < 0;
                Assert.Equal(value, reader.GetValue(numericFields[i]));
            }

            Assert.Equal(cells[15], reader.GetValue("TEXT1", 1));
            Assert.Equal(cells[16], reader.GetValue("TEXT1", 2));
            Assert.Equal(cells[15] + cells[16], reader.GetValue("TEXT2"));
        }

        Assert.False(reader.Read());
        Assert.Equal(2000, reader.Statistics.Records);
        Assert.True(sawNegative);
        Assert.Equal(0, reader.Statistics.InvalidFields + reader.Statistics.BlankNumericFields);
    }

    /// <summary>
    /// golden.bin was written by GnuCOBOL 3.1.2 from golden.txt, so the expected values come
    /// from plain text and the decoder is checked against a real compiler.
    /// </summary>
    [Fact]
    public void GnuCobol_file_matches_the_text_it_was_written_from()
    {
        string[] expected = File.ReadAllLines(TestData.Sample("gnucobol", "golden.txt"));
        using CopybookReader reader = CopybookReader.Open(
            TestData.Sample("gnucobol", "golden.bin"),
            Copybook.Load(TestData.Sample("gnucobol", "GOLDREC.cpy")),
            new ReadOptions { Encoding = Encoding.ASCII });
        Assert.Equal(68, reader.Copybook.Length);

        foreach (string line in expected)
        {
            Assert.True(reader.Read());
            Assert.Equal(long.Parse(line.AsSpan(0, 4), CultureInfo.InvariantCulture), reader.GetValue("G-CASE"));
            Assert.Equal(Scaled(line, 4, 14, 2), reader.GetValue("G-PACK-ODD"));
            Assert.Equal(Scaled(line, 18, 13, 2), reader.GetValue("G-PACK-EVEN"));
            Assert.Equal(long.Parse(line.AsSpan(31, 5), CultureInfo.InvariantCulture), reader.GetValue("G-PACK-UNS"));
            Assert.Equal(long.Parse(line.AsSpan(36, 19), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), reader.GetValue("G-PACK-MAX"));
            decimal display = Scaled(line, 55, 8, 2);
            Assert.Equal(display, reader.GetValue("G-DISP-OVER"));
            Assert.Equal(display, reader.GetValue("G-DISP-LEAD"));
            Assert.Equal(display, reader.GetValue("G-DISP-TRAIL"));
            Assert.Equal(long.Parse(line.AsSpan(63, 5), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), reader.GetValue("G-BIN-4"));
            Assert.Equal(long.Parse(line.AsSpan(68, 10), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), reader.GetValue("G-BIN-9"));
            Assert.Equal(long.Parse(line.AsSpan(78, 19), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), reader.GetValue("G-BIN-18"));
        }

        Assert.False(reader.Read());
        Assert.Equal(500, reader.Statistics.Records);
        Assert.Equal(0, reader.Statistics.InvalidFields + reader.Statistics.BlankNumericFields);
    }

    [Fact]
    public void Every_sample_copybook_parses()
    {
        string[] copybooks =
        [
            .. Directory.GetFiles(TestData.Sample("carddemo", "cpy"), "*.cpy"),
            .. Directory.GetFiles(TestData.Sample("aws-mdu"), "*.cpy"),
            .. Directory.GetFiles(TestData.Sample("gnucobol"), "*.cpy"),
        ];

        Assert.Equal(16, copybooks.Length);
        foreach (string path in copybooks)
        {
            Copybook copybook = Copybook.Load(path);
            Assert.True(copybook.Length > 0, path);
        }

        Assert.Equal(500, CardDemoCopybook("CVEXPORT").Length);
        Assert.Equal(500, Copybook.Load(TestData.Sample("aws-mdu", "COBKS05.cpy")).Length);
        Assert.Equal(306, Copybook.Load(TestData.Sample("aws-mdu", "COBVBFM2.cpy")).Length);
    }

    private static decimal Scaled(string line, int start, int length, int scale) =>
        decimal.Parse(line.AsSpan(start, length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) / (decimal)Math.Pow(10, scale);

    private static Copybook CardDemoCopybook(string name) => Copybook.Load(TestData.Sample("carddemo", "cpy", name + ".cpy"));

    private static string CardDemoEbcdic(string name) => TestData.Sample("carddemo", "ebcdic", $"AWS.M2.CARDDEMO.{name}.PS");

    private static List<object?[]> ReadAll(string path, Copybook copybook, ReadOptions options, out ReadStatistics statistics)
    {
        using CopybookReader reader = CopybookReader.Open(path, copybook, options);
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            rows.Add([.. reader.GetValues()]);
        }

        statistics = reader.Statistics;
        return rows;
    }

    /// <summary>Reads a CardDemo file into rows of column name to value, indexed by one column's text.</summary>
    private static Dictionary<string, Dictionary<string, object?>> Index(string file, string copybookName, string keyColumn)
    {
        using CopybookReader reader = CopybookReader.Open(CardDemoEbcdic(file), CardDemoCopybook(copybookName));
        var rows = new Dictionary<string, Dictionary<string, object?>>();
        while (reader.Read())
        {
            IReadOnlyList<object?> values = reader.GetValues();
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < values.Count; i++)
            {
                row[reader.Schema.Columns[i].Name] = values[i];
            }

            rows.Add(reader.GetString(keyColumn)!, row);
        }

        return rows;
    }
}
