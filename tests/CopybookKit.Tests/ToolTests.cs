using System.Diagnostics;
using System.Text;

namespace CopybookKit.Tests;

/// <summary>Runs the real command-line tool as a separate process.</summary>
public class ToolTests
{
    private static readonly string AccountData = TestData.Sample("carddemo", "ebcdic", "AWS.M2.CARDDEMO.ACCTDATA.PS");
    private static readonly string AccountCopybook = TestData.Sample("carddemo", "cpy", "CVACT01Y.cpy");
    private static readonly string ExportData = TestData.Sample("carddemo", "ebcdic", "AWS.M2.CARDDEMO.EXPORT.DATA.PS");
    private static readonly string ExportCopybook = TestData.Sample("carddemo", "cpy", "CVEXPORT.cpy");

    [Fact]
    public void Describe_lists_every_field_with_its_offset_and_size()
    {
        (int exit, string output, string error) = Run("describe", ExportCopybook);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
        Assert.Contains("Level  Name", output);
        Assert.Matches(@"10\s+EXP-ACCT-CURR-BAL\s+52\s+7\s+signed packed decimal\(12,2\)", output);
        Assert.Matches(@"10\s+EXP-CUST-ADDR-LINES\s+119\s+50\s+group\s+3", output);
        Assert.Matches(@"05\s+EXPORT-CUSTOMER-DATA\s+40\s+460\s+group\s+EXPORT-RECORD-DATA", output);
        Assert.Contains("Record EXPORT-RECORD: 500 bytes.", output);
    }

    [Fact]
    public void Describe_marks_offsets_that_follow_a_variable_table()
    {
        (int exit, string output, _) = Run("describe", TestData.Sample("aws-mdu", "COBVBFM2.cpy"));

        Assert.Equal(0, exit);
        Assert.Matches(@"03\s+OUT-REC\s+6\s+30\s+group\s+1 to 10 on OUT-REC-CNT", output);
        Assert.DoesNotContain("Offsets marked +", output);
    }

    [Fact]
    public void Convert_writes_json_lines_to_standard_output_and_a_summary_to_standard_error()
    {
        (int exit, string output, string error) = Run("convert", AccountData, "--copybook", AccountCopybook, "--limit", "3");

        Assert.Equal(0, exit);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("""{"ACCT-ID":1,"ACCT-ACTIVE-STATUS":"Y","ACCT-CURR-BAL":194.00,""", lines[0]);
        Assert.Equal("Converted 3 records.", error.Trim());
    }

    [Fact]
    public void Convert_writes_csv_to_a_file()
    {
        string path = Path.Combine(Path.GetTempPath(), "copybookkit-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            (int exit, string output, string error) = Run("convert", AccountData, "--copybook", AccountCopybook, "--to", "csv", "--out", path);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, output);
            Assert.Equal("Converted 50 records.", error.Trim());
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(51, lines.Length);
            Assert.StartsWith("ACCT-ID,ACCT-ACTIVE-STATUS,ACCT-CURR-BAL,", lines[0]);
            Assert.StartsWith("1,Y,194.00,2020.00,1020.00,2014-11-20,", lines[1]);
            Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Convert_reads_ascii_text_files()
    {
        (int exit, string output, _) = Run(
            "convert", TestData.Sample("carddemo", "ascii", "acctdata.txt"), "--copybook", AccountCopybook, "--encoding", "ascii", "--recfm", "text", "--limit", "1");

        Assert.Equal(0, exit);
        Assert.StartsWith("""{"ACCT-ID":1,"ACCT-ACTIVE-STATUS":"Y","ACCT-CURR-BAL":194.00,""", output);
    }

    [Fact]
    public void Convert_picks_a_layout_per_record_with_select()
    {
        const string rule = "EXPORT-REC-TYPE:C=EXPORT-CUSTOMER-DATA,A=EXPORT-ACCOUNT-DATA,T=EXPORT-TRANSACTION-DATA,X=EXPORT-CARD-XREF-DATA,D=EXPORT-CARD-DATA";

        (int exit, string output, string error) = Run("convert", ExportData, "--copybook", ExportCopybook, "--select", rule);

        Assert.Equal(0, exit);
        Assert.Equal("Converted 500 records.", error.Trim());
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(500, lines.Length);
        Assert.Equal(300, lines.Count(line => line.Contains("\"EXPORT-TRANSACTION-DATA\":{")));
        Assert.DoesNotContain(lines, line => line.Contains("\"EXPORT-RECORD-DATA\""));
    }

    [Fact]
    public void Convert_warns_about_invalid_numbers_and_suggests_select()
    {
        (int exit, _, string error) = Run("convert", ExportData, "--copybook", ExportCopybook, "--to", "csv");

        Assert.Equal(0, exit);
        Assert.Contains("Converted 500 records.", error);
        Assert.Contains("numeric fields were not valid numbers and were written as null", error);
        Assert.Contains("Record 1: EXP-ACCT-ID is not a valid zoned decimal number", error);
        Assert.Contains("--select", error);
    }

    [Fact]
    public void Strict_mode_fails_on_the_first_invalid_number()
    {
        (int exit, _, string error) = Run("convert", ExportData, "--copybook", ExportCopybook, "--strict");

        Assert.Equal(1, exit);
        Assert.StartsWith("copybook: Record 1: EXP-ACCT-ID is not a valid zoned decimal number", error);
    }

    [Fact]
    public void A_wrong_record_length_is_reported_as_a_failure()
    {
        (int exit, _, string error) = Run("convert", AccountData, "--copybook", AccountCopybook, "--record-length", "301");

        Assert.Equal(1, exit);
        Assert.Contains("The file has 15000 bytes, which is not a whole number of 301-byte records: 251 bytes are left over.", error);
    }

    [Theory]
    [InlineData("has no item named NOPE", "convert", "{data}", "--copybook", "{copybook}", "--select", "NOPE:1=ACCT-ID")]
    [InlineData("is not available", "convert", "{data}", "--copybook", "{copybook}", "--encoding", "cp99999")]
    [InlineData("Could not find file", "convert", "{data}", "--copybook", "missing.cpy")]
    [InlineData("Could not find file", "describe", "missing.cpy")]
    public void Problems_with_the_input_exit_with_1(string message, params string[] args)
    {
        (int exit, string output, string error) = Run(Fill(args));

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, output);
        Assert.Contains(message, error);
    }

    [Theory]
    [InlineData("Unknown command 'frobnicate'", "frobnicate")]
    [InlineData("--copybook is required", "convert", "{data}")]
    [InlineData("Unknown option --colour", "convert", "{data}", "--copybook", "{copybook}", "--colour", "red")]
    [InlineData("--to must be one of: json, csv", "convert", "{data}", "--copybook", "{copybook}", "--to", "xml")]
    [InlineData("--limit must be a whole number", "convert", "{data}", "--copybook", "{copybook}", "--limit", "few")]
    [InlineData("--limit needs a value", "convert", "{data}", "--copybook", "{copybook}", "--limit")]
    [InlineData("should look like FIELD:VALUE=ITEM", "convert", "{data}", "--copybook", "{copybook}", "--select", "ACCT-ID")]
    [InlineData("convert takes one data file", "convert", "--copybook", "{copybook}")]
    [InlineData("describe takes one copybook file", "describe")]
    [InlineData("--out names one of the input files", "convert", "{data}", "--copybook", "{copybook}", "--out", "{data}")]
    [InlineData("--out names one of the input files", "convert", "{data}", "--copybook", "{copybook}", "--out", "{copybook}")]
    public void Mistakes_on_the_command_line_exit_with_2(string message, params string[] args)
    {
        (int exit, string output, string error) = Run(Fill(args));

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, output);
        Assert.Contains(message, error);
        Assert.Contains("copybook --help", error);
    }

    [Fact]
    public void Help_and_version_are_printed()
    {
        (int exit, string output, _) = Run("--help");
        Assert.Equal(0, exit);
        Assert.Contains("copybook convert <data-file> --copybook <copybook> [options]", output);

        (exit, string bare, _) = Run();
        Assert.Equal(0, exit);
        Assert.Equal(output, bare);

        (exit, string version, _) = Run("--version");
        Assert.Equal(0, exit);
        Assert.Matches(@"^\d+\.\d+\.\d+", version);
    }

    [Theory]
    [InlineData("01 R.\n 05 F PIC XX(2147483647).", "is too long")]
    [InlineData("01 R.\n 05 FILLER PIC X OCCURS 2000000000 TIMES.", "expands to more than")]
    [InlineData("01 R.\n 05 F PIC X(1000000) OCCURS 1000 TIMES.", "--max-record-length")]
    public void A_copybook_built_to_waste_memory_is_refused_with_a_plain_message(string copybook, string message)
    {
        string folder = Directory.CreateTempSubdirectory("copybookkit-").FullName;
        try
        {
            string copybookPath = Path.Combine(folder, "hostile.cpy");
            string dataPath = Path.Combine(folder, "empty.dat");
            File.WriteAllText(copybookPath, copybook);
            File.WriteAllBytes(dataPath, []);

            (int exit, string output, string error) = Run("convert", dataPath, "--copybook", copybookPath);

            Assert.Equal(1, exit);
            Assert.Equal(string.Empty, output);
            Assert.Contains(message, error);
            Assert.DoesNotContain("   at ", error);
            Assert.DoesNotContain("Exception", error);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Csv_safe_marks_text_a_spreadsheet_would_run_and_the_record_limit_can_be_set()
    {
        string folder = Directory.CreateTempSubdirectory("copybookkit-").FullName;
        try
        {
            string copybookPath = Path.Combine(folder, "layout.cpy");
            string dataPath = Path.Combine(folder, "data.txt");
            File.WriteAllText(copybookPath, "01 R.\n 05 A PIC X(8).\n 05 B PIC S9(3) SIGN LEADING SEPARATE.");
            File.WriteAllText(dataPath, "=1+1    -005\n@cmd    +010\n");
            string[] common = ["convert", dataPath, "--copybook", copybookPath, "--encoding", "ascii", "--recfm", "text", "--to", "csv"];

            (int exit, string output, _) = Run([.. common, "--csv-safe"]);
            Assert.Equal(0, exit);
            Assert.Equal("A,B\n'=1+1,-5\n'@cmd,10\n", output);

            (exit, output, _) = Run(common);
            Assert.Equal(0, exit);
            Assert.Equal("A,B\n=1+1,-5\n@cmd,10\n", output);

            (exit, _, string error) = Run([.. common, "--max-record-length", "11"]);
            Assert.Equal(1, exit);
            Assert.Contains("more than the limit of 11", error);

            (exit, _, _) = Run([.. common, "--max-record-length", "12"]);
            Assert.Equal(0, exit);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string[] Fill(string[] args) =>
        [.. args.Select(arg => arg.Replace("{data}", AccountData).Replace("{copybook}", AccountCopybook))];

    private static (int ExitCode, string Output, string Error) Run(params string[] args)
    {
        // The tool is built next to the tests: .../tests/CopybookKit.Tests/bin/<configuration>/<framework>/
        string[] parts = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string framework = parts[^1];
        string configuration = parts[^2];
        string root = string.Join(Path.DirectorySeparatorChar, parts[..^5]);
        string tool = Path.Combine(root, "src", "CopybookKit.Tool", "bin", configuration, framework, "copybook.dll");
        Assert.True(File.Exists(tool), $"The tool was not built at {tool}.");

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(tool);
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "The tool did not finish in a minute.");
        return (process.ExitCode, output.Result.ReplaceLineEndings("\n"), error.Result.ReplaceLineEndings("\n"));
    }
}
