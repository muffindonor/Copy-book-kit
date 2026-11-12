using System.Globalization;
using System.Text;

namespace CopybookKit.Tool;

internal static class DescribeCommand
{
    public static int Run(Arguments arguments)
    {
        if (arguments.Positional.Count != 2)
        {
            throw new UsageException("describe takes one copybook file.");
        }

        arguments.RejectUnknown();
        Copybook copybook = Copybook.Load(arguments.Positional[1]);

        var rows = new List<string[]> { new[] { "Level", "Name", "Offset", "Size", "Type", "Occurs", "Redefines" } };
        foreach (DataItem item in copybook.Items)
        {
            int depth = 0;
            for (DataItem? parent = item.Parent; parent is not null; parent = parent.Parent)
            {
                depth++;
            }

            rows.Add(
            [
                item.Level.ToString("00", CultureInfo.InvariantCulture),
                new string(' ', depth * 2) + item.Name,
                item.Offset.ToString(CultureInfo.InvariantCulture) + (item.IsVariablyLocated ? "+" : string.Empty),
                item.Size.ToString(CultureInfo.InvariantCulture),
                DescribeType(item),
                DescribeOccurs(item),
                item.Redefines?.Name ?? string.Empty,
            ]);
        }

        int[] widths = new int[rows[0].Length];
        foreach (string[] row in rows)
        {
            for (int i = 0; i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        foreach (string[] row in rows)
        {
            var line = new StringBuilder();
            for (int i = 0; i < row.Length; i++)
            {
                // Offset and Size are numbers, so they line up on the right.
                line.Append(i is 2 or 3 ? row[i].PadLeft(widths[i]) : row[i].PadRight(widths[i])).Append("  ");
            }

            Console.WriteLine(line.ToString().TrimEnd());
        }

        Console.WriteLine();
        foreach (DataItem record in copybook.Records)
        {
            Console.WriteLine($"Record {record.Name}: {record.Size} bytes.");
        }

        if (copybook.Items.Any(item => item.IsVariablyLocated))
        {
            Console.WriteLine("Offsets marked + follow a variable-length table, and assume it is at its largest.");
        }

        return 0;
    }

    private static string DescribeType(DataItem item)
    {
        string sign = item.IsSigned ? "signed " : string.Empty;
        string digits = item.Scale > 0 ? $"({item.Digits},{item.Scale})" : $"({item.Digits})";
        return item.Kind switch
        {
            ItemKind.Group => "group",
            ItemKind.Text => $"text  {item.Picture}",
            ItemKind.ZonedDecimal => $"{sign}zoned decimal{digits}",
            ItemKind.PackedDecimal => $"{sign}packed decimal{digits}",
            ItemKind.Binary => $"{sign}binary{digits}",
            ItemKind.Float => "float",
            _ => "double",
        };
    }

    private static string DescribeOccurs(DataItem item)
    {
        if (!item.HasOccurs)
        {
            return string.Empty;
        }

        return item.DependingOn is { } counter
            ? $"{item.MinOccurs} to {item.MaxOccurs} on {counter.Name}"
            : item.MaxOccurs.ToString(CultureInfo.InvariantCulture);
    }
}

internal static class ConvertCommand
{
    public static int Run(Arguments arguments)
    {
        if (arguments.Positional.Count != 2)
        {
            throw new UsageException("convert takes one data file.");
        }

        string dataPath = arguments.Positional[1];
        string copybookPath = arguments.Required("copybook");
        string format = arguments.Choice("to", "json", ("json", "json"), ("csv", "csv"));
        string? outPath = arguments.Text("out");
        long? limit = arguments.Number("limit");
        bool csvSafe = arguments.Flag("csv-safe");
        long? recordLength = arguments.Number("record-length");
        if (recordLength is 0 or > int.MaxValue)
        {
            throw new UsageException("--record-length must be at least 1.");
        }

        long? maxRecordLength = arguments.Number("max-record-length");
        if (maxRecordLength is 0 or > int.MaxValue)
        {
            throw new UsageException("--max-record-length must be between 1 and 2147483647.");
        }

        var options = new ReadOptions
        {
            Format = arguments.Choice(
                "recfm",
                RecordFormat.Fixed,
                ("fixed", RecordFormat.Fixed),
                ("rdw", RecordFormat.VariableRdw),
                ("text", RecordFormat.LineSequential)),
            RecordLength = (int?)recordLength,
            MaxRecordLength = (int)(maxRecordLength ?? new ReadOptions().MaxRecordLength),
            RecordName = arguments.Text("record"),
            Trim = arguments.Choice("trim", TextTrim.Right, ("right", TextTrim.Right), ("both", TextTrim.Both), ("none", TextTrim.None)),
            InvalidData = arguments.Flag("strict") ? InvalidDataPolicy.Throw : InvalidDataPolicy.Null,
            BinaryByteOrder = arguments.Flag("little-endian") ? ByteOrder.LittleEndian : ByteOrder.BigEndian,
            FloatFormat = arguments.Choice<FloatFormat?>("float", null, ("ibm", FloatFormat.IbmHex), ("ieee", FloatFormat.Ieee)),
        };

        if (arguments.Text("encoding") is { } encoding)
        {
            options.Encoding = EbcdicEncodings.Parse(encoding);
        }

        foreach (string rule in arguments.All("select"))
        {
            options.Selectors.Add(ParseRule(rule));
        }

        arguments.RejectUnknown();
        if (outPath is not null && (IsSameFile(outPath, dataPath) || IsSameFile(outPath, copybookPath)))
        {
            throw new UsageException("--out names one of the input files, which would overwrite it.");
        }

        Copybook copybook = Copybook.Load(copybookPath);
        bool unselectedAlternatives = options.Selectors.Count == 0 && copybook.Items.Any(item => item.Redefines is not null);
        using CopybookReader reader = CopybookReader.Open(dataPath, copybook, options);
        using Stream output = outPath is null
            ? Console.OpenStandardOutput()
            : new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

        long written;
        if (format == "csv")
        {
            using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1 << 16, leaveOpen: true);
            written = reader.WriteCsv(writer, limit, csvSafe);
        }
        else
        {
            written = reader.WriteJsonLines(output, limit);
        }

        output.Flush();
        Report(reader.Statistics, written, unselectedAlternatives);
        return 0;
    }

    private static bool IsSameFile(string first, string second) =>
        Path.GetFullPath(first).Equals(
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Parses FIELD:VALUE=ITEM,VALUE=ITEM.</summary>
    private static RedefineRule ParseRule(string text)
    {
        int colon = text.IndexOf(':');
        if (colon <= 0)
        {
            throw new UsageException($"--select '{text}' should look like FIELD:VALUE=ITEM,VALUE=ITEM.");
        }

        var cases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in text[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.LastIndexOf('=');
            if (equals <= 0 || equals == pair.Length - 1)
            {
                throw new UsageException($"--select '{text}' should look like FIELD:VALUE=ITEM,VALUE=ITEM.");
            }

            cases[pair[..equals].Trim()] = pair[(equals + 1)..].Trim();
        }

        if (cases.Count == 0)
        {
            throw new UsageException($"--select '{text}' lists no values.");
        }

        return new RedefineRule(text[..colon].Trim(), cases);
    }

    private static void Report(ReadStatistics statistics, long written, bool unselectedAlternatives)
    {
        TextWriter log = Console.Error;
        log.WriteLine($"Converted {written:N0} records.");
        if (statistics.InvalidFields > 0)
        {
            log.WriteLine($"Warning: {statistics.InvalidFields:N0} numeric fields were not valid numbers and were written as null.");
            foreach (string issue in statistics.Issues)
            {
                log.WriteLine($"  {issue}");
            }

            if (unselectedAlternatives)
            {
                log.WriteLine("  The copybook has REDEFINES, so every alternative layout was tried on every record.");
                log.WriteLine("  If the file holds several record types, pick one layout per record with --select.");
            }
            else
            {
                log.WriteLine("  This often means the wrong copybook, record length or code page. Use --strict to stop at the first one.");
            }
        }

        if (statistics.BlankNumericFields > 0)
        {
            log.WriteLine($"Note: {statistics.BlankNumericFields:N0} numeric fields were blank and were written as null.");
        }

        if (statistics.MissingFields > 0)
        {
            log.WriteLine($"Note: {statistics.MissingFields:N0} fields lay beyond the end of a short record and were written as null.");
        }

        if (statistics.UnmatchedRecords > 0)
        {
            log.WriteLine($"Note: {statistics.UnmatchedRecords:N0} records matched no --select value, so every alternative was decoded.");
        }
    }
}
