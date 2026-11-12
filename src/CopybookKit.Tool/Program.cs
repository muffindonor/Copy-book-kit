using System.Reflection;
using System.Text;
using CopybookKit;
using CopybookKit.Tool;

const int Success = 0;
const int Failure = 1;
const int UsageError = 2;

HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase) { "help", "version", "strict", "little-endian", "csv-safe" };

try
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    var arguments = new Arguments(args, flags);
    if (arguments.Flag("version"))
    {
        Console.WriteLine(typeof(Copybook).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]);
        return Success;
    }

    if (arguments.Flag("help") || arguments.Positional.Count == 0)
    {
        Console.WriteLine(Help.Text);
        return Success;
    }

    string command = arguments.Positional[0].ToLowerInvariant();
    return command switch
    {
        "describe" => DescribeCommand.Run(arguments),
        "convert" => ConvertCommand.Run(arguments),
        _ => throw new UsageException($"Unknown command '{arguments.Positional[0]}'."),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine($"copybook: {ex.Message}");
    Console.Error.WriteLine("Run 'copybook --help' for usage.");
    return UsageError;
}
catch (Exception ex) when (ex is CopybookException or RecordFormatException or IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"copybook: {ex.Message}");
    return Failure;
}

internal static class Help
{
    public const string Text = """
        copybook - read mainframe data files with a COBOL copybook

        Usage:
          copybook describe <copybook>
          copybook convert <data-file> --copybook <copybook> [options]

        Commands:
          describe   Show each field's offset, size and type.
          convert    Convert a data file to JSON Lines or CSV.

        Options for convert:
          --copybook <file>     The copybook that describes the records. Required.
          --to <format>         json (default) or csv. JSON keeps groups and tables nested.
          --out <file>          Write here. Defaults to standard output.
          --encoding <name>     Character set of the data: cp037 (default), cp1047, cp500, cp1140,
                                cp424, any other IBM code page number, or ascii, latin1, utf-8.
          --recfm <format>      fixed (default), rdw (variable-length records that start with a
                                4-byte record descriptor word) or text (one record per line).
          --record-length <n>   Record length for fixed. Defaults to the copybook's length.
          --max-record-length <bytes>
                                The longest record to accept. Defaults to 16777216 (16 MB).
          --record <name>       Which 01-level record to use when the copybook has several.
          --select <rule>       Pick the layout per record. FIELD:VALUE=ITEM,VALUE=ITEM
                                decodes only ITEM, out of its REDEFINES alternatives, when FIELD
                                has VALUE. May be repeated.
          --limit <n>           Stop after n records.
          --trim <mode>         right (default), both or none.
          --strict              Fail on the first invalid number. By default it becomes null
                                and is counted.
          --little-endian       Binary fields are least significant byte first.
          --csv-safe            In CSV, put an apostrophe before text that a spreadsheet would run
                                as a formula. Use it for data you don't trust.
          --float <format>      ibm or ieee, for COMP-1 and COMP-2. Defaults to ibm for EBCDIC.

        Examples:
          copybook describe CUSTOMER.cpy
          copybook convert CUSTOMER.DAT --copybook CUSTOMER.cpy --to csv --out customer.csv
          copybook convert CLIENT.DAT --copybook CLIENT.cpy --select "CLIENT-TYPE:1=CLIENT-MAIN,2=CLIENT-ADDRESS"
        """;
}
