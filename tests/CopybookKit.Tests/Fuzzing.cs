using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CopybookKit.Tests;

/// <summary>
/// Random testing. A generator invents copybooks and writes records for them with its own
/// encoder, which shares no code with the library, so the reader can be checked against it.
/// The same copybooks are then damaged, and fed damaged data, to check that hostile input
/// only ever produces the documented exceptions, quickly and without large allocations.
/// </summary>
internal static class Fuzzing
{
    /// <summary>No single case may take longer than this.</summary>
    private static readonly TimeSpan CaseTimeLimit = TimeSpan.FromSeconds(10);

    private static readonly string[] Fragments =
    [
        " OCCURS 999999999 TIMES", " OCCURS 0 TO 2147483647 TIMES DEPENDING ON ", " REDEFINES ", " PIC X(2147483647)",
        " PIC XX(2147483647)", " PIC 9(2147483647)", " PIC S9(18)V9(18)", " PIC X(1048576)", " COMP-3", " COMP", " COMP-5",
        " COMP-1", " BINARY", " 88 ", " 66 ", " 77 ", " 01 ", " 05 ", " 49 ", "(", ")", ".", ". ", " SYNC", " VALUE 'x",
        " VALUE \"", " COPY X.", " FILLER", " SIGN LEADING SEPARATE", " INDEX", " OCCURS 1000000", " OCCURS 4000 TIMES",
        " PIC +9(2)", " PIC ( )", "\n", "\n      -", "\u001b[31m", "\0", "\t", " PIC P(30)9", " PIC 9P(30)", " POINTER",
        " DEPENDING ON ", " TO ", " TIMES", " PIC ", " USAGE ", " IS ", "*>", "'", "\"",
    ];

    /// <summary>What the cases have exercised so far, to show that the generator reaches what it is meant to.</summary>
    public static FuzzStatistics Statistics { get; } = new();

    private enum Usage
    {
        Text,
        Zoned,
        Packed,
        Binary,
        NativeBinary,
        Float,
        Double,
    }

    private enum SignKind
    {
        Trailing,
        Leading,
        TrailingSeparate,
        LeadingSeparate,
    }

    /// <summary>Runs every kind of case for one seed. Throws with the seed in the message when a case fails.</summary>
    public static void RunCase(int seed)
    {
        var random = new Random(seed);
        bool ascii = random.Next(5) == 0;
        Node record = new Generator(random, ascii).Record();
        bool fixedSource = random.Next(2) == 0;
        string source = Render(record, fixedSource);

        Guard(seed, "round trip", source, () => RoundTrip(record, source, fixedSource, ascii, random));
        Guard(seed, "damaged data", source, () => DamagedData(record, source, ascii, random));
        for (int i = 0; i < 4; i++)
        {
            string damaged = Damage(source, random);
            Guard(seed, "damaged copybook", damaged, () => DamagedCopybook(damaged, random));
        }
    }

    /// <summary>Damages a real copybook and checks that parsing and reading still behave.</summary>
    public static void RunSampleCase(string source, int seed)
    {
        var random = new Random(seed);
        string damaged = Damage(source, random);
        Guard(seed, "damaged sample copybook", damaged, () => DamagedCopybook(damaged, random));
    }

    private static void Guard(int seed, string what, string source, Action action)
    {
        var watch = Stopwatch.StartNew();
        long before = GC.GetTotalAllocatedBytes();
        try
        {
            action();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed {seed}, {what}: {ex.GetType().Name}: {ex.Message}\n--- copybook ---\n{source}\n---", ex);
        }

        long allocated = GC.GetTotalAllocatedBytes() - before;
        if (watch.Elapsed > CaseTimeLimit || allocated > 1L << 30)
        {
            throw new InvalidOperationException(
                $"Seed {seed}, {what}: took {watch.Elapsed.TotalSeconds:F1} s and allocated {allocated >> 20} MB.\n--- copybook ---\n{source}\n---");
        }
    }

    // ---------------------------------------------------------------- round trip

    private static void RoundTrip(Node record, string source, bool fixedSource, bool ascii, Random random)
    {
        Copybook copybook = Copybook.Parse(source, fixedSource ? SourceFormat.Fixed : SourceFormat.Auto);
        var counters = new List<Node>();
        Collect(record, counters);

        // The library's layout must agree with a record written at its largest.
        var largest = new Encoder(ascii, random, counters.ToDictionary(c => c, c => c.CounterFor!.Occurs));
        largest.Item(record, []);
        Check(copybook.Length == largest.Bytes.Count, $"The copybook's length is {copybook.Length}, but its largest record has {largest.Bytes.Count} bytes.");
        if (largest.Bytes.Count > 60_000)
        {
            return;
        }

        bool variable = counters.Count > 0;
        int recordCount = random.Next(1, 4);
        var file = new MemoryStream();
        var expected = new List<List<Expected>>();
        for (int i = 0; i < recordCount; i++)
        {
            var encoder = new Encoder(ascii, random, counters.ToDictionary(c => c, c => random.Next(c.CounterFor!.Occurs + 1)));
            encoder.Item(record, []);
            expected.Add(encoder.Expected);
            byte[] bytes = [.. encoder.Bytes];
            if (ascii)
            {
                file.Write(bytes);
                file.Write(random.Next(2) == 0 ? "\n"u8 : "\r\n"u8);
            }
            else if (variable)
            {
                byte[] descriptor = new byte[4];
                BinaryPrimitives.WriteUInt16BigEndian(descriptor, (ushort)(bytes.Length + 4));
                file.Write(descriptor);
                file.Write(bytes);
            }
            else
            {
                file.Write(bytes);
            }
        }

        var options = new ReadOptions
        {
            Encoding = ascii ? Encoding.ASCII : EbcdicEncodings.Cp037,
            Format = ascii ? RecordFormat.LineSequential : variable ? RecordFormat.VariableRdw : RecordFormat.Fixed,
            FloatFormat = FloatFormat.Ieee,
        };

        file.Position = 0;
        using var reader = new CopybookReader(file, copybook, options);
        foreach (List<Expected> record1 in expected)
        {
            Check(reader.Read(), "The file ended early.");
            bool jsonFirst = random.Next(2) == 0;
            string json = jsonFirst ? reader.ToJson() : string.Empty;
            foreach (Expected field in record1)
            {
                object? actual = reader.GetValue(field.Node.Name, field.Subscripts);
                if (field.Wild)
                {
                    continue;
                }

                Statistics.FieldsCompared++;
                Statistics.Usages[(int)field.Node.Usage]++;

                Check(
                    Same(field.Value, actual),
                    $"{field.Node.Name}({string.Join(',', field.Subscripts)}) should be {Show(field.Value)} but was {Show(actual)}.");
            }

            json = jsonFirst ? json : reader.ToJson();
            var leaves = new List<JsonElement>();
            using JsonDocument document = JsonDocument.Parse(json);
            Leaves(document.RootElement, leaves);
            List<Expected> present = [.. record1.Where(field => !field.Missing)];
            Check(leaves.Count == present.Count, $"The JSON has {leaves.Count} values but {present.Count} were written: {json}");
            for (int i = 0; i < present.Count; i++)
            {
                if (!present[i].Wild)
                {
                    Check(SameJson(present[i].Value, leaves[i]), $"JSON value {i} ({present[i].Node.Name}) should be {Show(present[i].Value)} but was {leaves[i].GetRawText()}.");
                }
            }

            IReadOnlyList<object?> row = reader.GetValues();
            Check(row.Count == reader.Schema.Columns.Count, "The row and the schema differ in length.");
        }

        Check(!reader.Read(), "The file has more records than were written.");
        Statistics.RecordsCompared += recordCount;
        Statistics.WithVariableTables += variable ? 1 : 0;
        Statistics.WithRedefines += source.Contains("REDEFINES", StringComparison.Ordinal) ? 1 : 0;
        Statistics.Ascii += ascii ? 1 : 0;
        Check(reader.Statistics.MissingFields == 0 || variable, "Fields were reported missing from a full record.");
    }

    private static void Collect(Node node, List<Node> counters)
    {
        if (node.CounterFor is not null)
        {
            counters.Add(node);
        }

        foreach (Node child in node.Children)
        {
            Collect(child, counters);
        }
    }

    private static void Leaves(JsonElement element, List<JsonElement> leaves)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Leaves(property.Value, leaves);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Leaves(item, leaves);
                }

                break;
            default:
                leaves.Add(element);
                break;
        }
    }

    private static bool Same(object? expected, object? actual) => (expected, actual) switch
    {
        (null, null) => true,
        (string e, string a) => e == a,
        (long e, long a) => e == a,
        (decimal e, decimal a) => e == a,
        (double e, double a) => e.Equals(a),
        _ => false,
    };

    private static bool SameJson(object? expected, JsonElement actual) => expected switch
    {
        null => actual.ValueKind == JsonValueKind.Null,
        string e => actual.ValueKind == JsonValueKind.String && actual.GetString() == e,
        long e => actual.ValueKind == JsonValueKind.Number && actual.GetDecimal() == e,
        decimal e => actual.ValueKind == JsonValueKind.Number && actual.GetDecimal() == e,
        double e => actual.ValueKind == JsonValueKind.Number && actual.GetDouble().Equals(e),
        _ => false,
    };

    private static string Show(object? value) => value switch
    {
        null => "null",
        string text => $"'{text}'",
        IFormattable number => $"{number.ToString(null, CultureInfo.InvariantCulture)} ({value.GetType().Name})",
        _ => value.ToString()!,
    };

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    // ---------------------------------------------------------------- hostile input

    /// <summary>Reads random and half-valid data with a valid copybook. Only <see cref="RecordFormatException"/> may escape.</summary>
    private static void DamagedData(Node record, string source, bool ascii, Random random)
    {
        Copybook copybook = Copybook.Parse(source);
        if (copybook.Length > 60_000)
        {
            return;
        }

        var counters = new List<Node>();
        Collect(record, counters);
        for (int round = 0; round < 3; round++)
        {
            var encoder = new Encoder(ascii, random, counters.ToDictionary(c => c, c => random.Next(c.CounterFor!.Occurs + 1)));
            encoder.Item(record, []);
            byte[] data = [.. encoder.Bytes];
            ReadWhatever(copybook, Mangle(data, copybook.Length, random), random);
        }
    }

    private static byte[] Mangle(byte[] valid, int recordLength, Random random)
    {
        var data = new List<byte>();
        int records = random.Next(4);
        for (int i = 0; i < records; i++)
        {
            byte[] copy = [.. valid];
            int flips = random.Next(4);
            for (int f = 0; f < flips && copy.Length > 0; f++)
            {
                copy[random.Next(copy.Length)] = (byte)random.Next(256);
            }

            switch (random.Next(6))
            {
                case 0:
                    random.NextBytes(copy);
                    break;
                case 1:
                    copy = copy[..random.Next(copy.Length + 1)];
                    break;
                case 2:
                    Array.Resize(ref copy, Math.Max(recordLength, copy.Length));
                    break;
            }

            if (random.Next(2) == 0)
            {
                // A record descriptor word, right or wrong.
                int length = random.Next(8) == 0 ? random.Next(65536) : copy.Length + 4;
                data.Add((byte)(length >> 8));
                data.Add((byte)length);
                data.Add((byte)(random.Next(20) == 0 ? 1 : 0));
                data.Add(0);
            }

            data.AddRange(copy);
            if (random.Next(3) == 0)
            {
                data.Add((byte)'\n');
            }
        }

        return [.. data];
    }

    private static void ReadWhatever(Copybook copybook, byte[] data, Random random)
    {
        var options = new ReadOptions
        {
            Format = (RecordFormat)random.Next(3),
            Trim = (TextTrim)random.Next(3),
            InvalidData = random.Next(4) == 0 ? InvalidDataPolicy.Throw : InvalidDataPolicy.Null,
            BinaryByteOrder = (ByteOrder)random.Next(2),
            Encoding = random.Next(4) switch
            {
                0 => Encoding.ASCII,
                1 => Encoding.UTF8,
                2 => EbcdicEncodings.Cp1047,
                _ => EbcdicEncodings.Cp037,
            },
        };

        CopybookReader reader;
        try
        {
            reader = new CopybookReader(new MemoryStream(data), copybook, options);
        }
        catch (Exception ex) when (ex is RecordFormatException or ArgumentException)
        {
            // A file that isn't a whole number of records, or a layout too large to read.
            return;
        }

        using (reader)
        {
            try
            {
                switch (random.Next(3))
                {
                    case 0:
                        reader.WriteCsv(TextWriter.Null);
                        break;
                    case 1:
                        reader.WriteJsonLines(Stream.Null);
                        break;
                    default:
                        while (reader.Read())
                        {
                            using JsonDocument document = JsonDocument.Parse(reader.ToJson());
                            _ = reader.GetValues();
                        }

                        break;
                }
            }
            catch (RecordFormatException)
            {
                Statistics.DataRejected++;
            }

            Statistics.DataReads++;
        }
    }

    /// <summary>Parses a damaged copybook. Only <see cref="CopybookException"/> may escape. If it parses, it must also read.</summary>
    private static void DamagedCopybook(string source, Random random)
    {
        Copybook copybook;
        try
        {
            copybook = Copybook.Parse(source, (SourceFormat)random.Next(3));
        }
        catch (CopybookException)
        {
            Statistics.CopybooksRejected++;
            return;
        }

        Statistics.DamagedCopybooksParsed++;
        _ = copybook.Length;
        byte[] data = new byte[random.Next(3) == 0 ? 0 : random.Next(1, 400)];
        random.NextBytes(data);
        ReadWhatever(copybook, data, random);
    }

    private static string Damage(string source, Random random)
    {
        const string Alphabet = " .()-'\"*\n\t0123456789XSVP9ABCZ,;+\u0001\u001b";
        var text = new StringBuilder(source);
        int changes = random.Next(1, 5);
        for (int i = 0; i < changes; i++)
        {
            int at = random.Next(text.Length + 1);
            switch (random.Next(6))
            {
                case 0 when at < text.Length:
                    text[at] = Alphabet[random.Next(Alphabet.Length)];
                    break;
                case 1 when at < text.Length:
                    text.Remove(at, Math.Min(random.Next(1, 30), text.Length - at));
                    break;
                case 2:
                    text.Insert(at, Alphabet[random.Next(Alphabet.Length)]);
                    break;
                case 3 when text.Length > 0:
                    int from = random.Next(text.Length);
                    string copy = text.ToString(from, Math.Min(random.Next(1, 80), text.Length - from));
                    text.Insert(at, copy);
                    break;
                default:
                    text.Insert(at, Fragments[random.Next(Fragments.Length)]);
                    break;
            }
        }

        return text.ToString();
    }

    // ---------------------------------------------------------------- the generator's own model

    private sealed class Node
    {
        public required string Name { get; init; }

        public required int Level { get; init; }

        public bool IsGroup { get; init; }

        public bool Filler { get; init; }

        public List<Node> Children { get; } = [];

        /// <summary>0 when the item has no OCCURS, otherwise the most occurrences.</summary>
        public int Occurs { get; set; }

        /// <summary>The field that says how many occurrences a record has.</summary>
        public Node? Counter { get; set; }

        /// <summary>Set on a counter: the table it counts.</summary>
        public Node? CounterFor { get; set; }

        public Node? Redefines { get; set; }

        public Usage Usage { get; init; }

        /// <summary>Characters for text, digits for numbers.</summary>
        public int Length { get; init; }

        public int Scale { get; init; }

        public bool Signed { get; init; }

        public SignKind Sign { get; init; }
    }

    private sealed record Expected(Node Node, int[] Subscripts, object? Value, bool Wild, bool Missing);

    private sealed class Generator(Random random, bool ascii)
    {
        private int _names;
        private Node _record = null!;

        public Node Record()
        {
            _record = new Node { Name = "REC", Level = 1, IsGroup = true };
            Fill(_record, 5, depth: 0, insideTable: false, plain: false);
            return _record;
        }

        private void Fill(Node group, int level, int depth, bool insideTable, bool plain)
        {
            int count = random.Next(1, 6);
            for (int i = 0; i < count; i++)
            {
                Node child = Item(level, depth, insideTable, plain);
                group.Children.Add(child);

                // Now and then, a second layout over the same bytes.
                if (!plain && !child.Filler && child.Occurs == 0 && !HasCounter(child) && random.Next(8) == 0)
                {
                    Node other = Item(level, depth, insideTable: true, plain: true);
                    other.Occurs = 0;
                    other.Redefines = child;
                    group.Children.Add(other);
                }
            }
        }

        private static bool HasCounter(Node node) => node.Counter is not null || node.Children.Any(HasCounter);

        private Node Item(int level, int depth, bool insideTable, bool plain)
        {
            bool filler = random.Next(10) == 0;
            string name = filler ? "FILLER" : $"F{++_names}";
            Node node;
            if (depth < 3 && random.Next(100) < 22)
            {
                node = new Node { Name = name, Level = level, IsGroup = true, Filler = filler };
                bool table = random.Next(3) == 0;
                Fill(node, level + 5, depth + 1, insideTable || table, plain);
                if (table)
                {
                    SetOccurs(node, insideTable, plain);
                }

                return node;
            }

            node = Field(name, level, filler);
            if (random.Next(5) == 0)
            {
                SetOccurs(node, insideTable, plain);
            }

            return node;
        }

        private void SetOccurs(Node node, bool insideTable, bool plain)
        {
            node.Occurs = random.Next(1, 4);
            if (!insideTable && !plain && !ascii && random.Next(2) == 0)
            {
                bool binary = random.Next(2) == 0;
                var counter = new Node
                {
                    Name = $"CNT{++_names}",
                    Level = 5,
                    Usage = binary ? Usage.Binary : Usage.Zoned,
                    Length = binary ? 4 : 2,
                    CounterFor = node,
                };
                node.Counter = counter;
                _record.Children.Insert(0, counter);
            }
        }

        private Node Field(string name, int level, bool filler)
        {
            int kind = ascii ? random.Next(2) : random.Next(7);
            Usage usage = kind switch
            {
                0 => Usage.Text,
                1 => Usage.Zoned,
                2 => Usage.Packed,
                3 => Usage.Binary,
                4 => Usage.NativeBinary,
                5 => Usage.Float,
                _ => Usage.Double,
            };

            int digits = usage switch
            {
                Usage.Text => random.Next(1, 13),
                Usage.Binary or Usage.NativeBinary => random.Next(1, 19),
                _ => random.Next(8) == 0 ? random.Next(19, 29) : random.Next(1, 19),
            };

            bool numeric = usage is Usage.Zoned or Usage.Packed or Usage.Binary or Usage.NativeBinary;
            return new Node
            {
                Name = name,
                Level = level,
                Filler = filler,
                Usage = usage,
                Length = digits,
                Scale = numeric && random.Next(3) == 0 ? random.Next(digits + 1) : 0,
                Signed = numeric && random.Next(2) == 0,
                Sign = (SignKind)random.Next(4),
            };
        }
    }

    private static string Render(Node record, bool fixedSource)
    {
        var text = new StringBuilder();
        int sequence = 100;
        Write(record, 0);
        return text.ToString();

        void Line(string code)
        {
            text.Append(fixedSource ? $"{sequence:000000} " : "  ").Append(code).Append('\n');
            sequence += 100;
        }

        void Write(Node node, int depth)
        {
            string indent = new(' ', depth * 2);
            var clauses = new List<string> { $"{node.Level:00} {node.Name}" };
            if (node.Redefines is not null)
            {
                clauses.Add($"REDEFINES {node.Redefines.Name}");
            }

            if (!node.IsGroup)
            {
                clauses.AddRange(FieldClauses(node));
            }

            if (node.Occurs > 0)
            {
                clauses.Add(node.Counter is null
                    ? $"OCCURS {node.Occurs} TIMES"
                    : $"OCCURS 0 TO {node.Occurs} TIMES DEPENDING ON {node.Counter.Name}");
            }

            if (fixedSource)
            {
                // One clause per line, as long entries are written in real copybooks.
                for (int i = 0; i < clauses.Count; i++)
                {
                    Line(indent + (i == 0 ? string.Empty : "    ") + clauses[i] + (i == clauses.Count - 1 ? "." : string.Empty));
                }
            }
            else
            {
                Line(indent + string.Join(' ', clauses) + ".");
            }

            foreach (Node child in node.Children)
            {
                Write(child, depth + 1);
            }
        }
    }

    private static IEnumerable<string> FieldClauses(Node node)
    {
        if (node.Usage == Usage.Float)
        {
            yield return "COMP-1";
            yield break;
        }

        if (node.Usage == Usage.Double)
        {
            yield return "USAGE IS COMP-2";
            yield break;
        }

        if (node.Usage == Usage.Text)
        {
            yield return node.Length <= 3 ? $"PIC {new string('X', node.Length)}" : $"PIC X({node.Length})";
            yield break;
        }

        int whole = node.Length - node.Scale;
        string picture = (node.Signed ? "S" : string.Empty)
            + (whole > 0 ? $"9({whole})" : string.Empty)
            + (node.Scale > 0 ? $"V9({node.Scale})" : string.Empty);
        yield return $"PIC {picture}";

        switch (node.Usage)
        {
            case Usage.Packed:
                yield return node.Length % 2 == 0 ? "COMP-3" : "USAGE PACKED-DECIMAL";
                break;
            case Usage.Binary:
                yield return (node.Length % 3) switch { 0 => "COMP", 1 => "BINARY", _ => "USAGE IS COMP-4" };
                break;
            case Usage.NativeBinary:
                yield return "COMP-5";
                break;
            case Usage.Zoned when node.Signed:
                switch (node.Sign)
                {
                    case SignKind.Leading:
                        yield return "SIGN IS LEADING";
                        break;
                    case SignKind.LeadingSeparate:
                        yield return "SIGN LEADING SEPARATE";
                        break;
                    case SignKind.TrailingSeparate:
                        yield return "SIGN IS TRAILING SEPARATE CHARACTER";
                        break;
                }

                break;
        }
    }

    /// <summary>Writes a record's bytes from the generator's model, and notes what each field should read back as.</summary>
    private sealed class Encoder(bool ascii, Random random, Dictionary<Node, int> counts)
    {
        private const string TextCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789-.$  ";

        public List<byte> Bytes { get; } = [];

        public List<Expected> Expected { get; } = [];

        private bool _wild;

        public void Item(Node node, int[] subscripts)
        {
            int most = Math.Max(node.Occurs, 1);
            int count = node.Counter is null ? most : counts[node.Counter];
            for (int i = 0; i < most; i++)
            {
                int[] own = node.Occurs > 0 ? [.. subscripts, i + 1] : subscripts;
                if (i >= count)
                {
                    Missing(node, own);
                }
                else if (node.IsGroup)
                {
                    Children(node, own);
                }
                else
                {
                    Field(node, own);
                }
            }
        }

        private void Missing(Node node, int[] subscripts)
        {
            if (!node.IsGroup)
            {
                if (!node.Filler)
                {
                    Expected.Add(new Expected(node, subscripts, null, Wild: false, Missing: true));
                }

                return;
            }

            foreach (Node child in node.Children)
            {
                for (int i = 0; i < Math.Max(child.Occurs, 1); i++)
                {
                    Missing(child, child.Occurs > 0 ? [.. subscripts, i + 1] : subscripts);
                }
            }
        }

        private void Children(Node group, int[] subscripts)
        {
            int start = 0;
            int end = 0;
            foreach (Node child in group.Children)
            {
                if (child.Redefines is null)
                {
                    start = Bytes.Count;
                    Item(child, subscripts);
                    end = Bytes.Count;
                    continue;
                }

                // The redefinition reads the bytes already written. Only its size and its place in the output are known.
                var shadow = new Encoder(ascii, random, counts) { _wild = true };
                shadow.Item(child, subscripts);
                Expected.AddRange(shadow.Expected);
                int needed = start + shadow.Bytes.Count;
                while (Bytes.Count < needed)
                {
                    Bytes.Add(ascii ? (byte)' ' : (byte)0x40);
                }

                end = Math.Max(end, needed);
            }
        }

        private void Field(Node node, int[] subscripts)
        {
            object? value;
            switch (node.Usage)
            {
                case Usage.Text:
                    var text = new StringBuilder();
                    for (int i = 0; i < node.Length; i++)
                    {
                        text.Append(TextCharacters[random.Next(TextCharacters.Length)]);
                    }

                    Bytes.AddRange((ascii ? Encoding.ASCII : EbcdicEncodings.Cp037).GetBytes(text.ToString()));
                    value = text.ToString().TrimEnd(' ');
                    break;

                case Usage.Float:
                    float single = (float)((random.NextDouble() - 0.5) * 1e6);
                    Span<byte> four = stackalloc byte[4];
                    BinaryPrimitives.WriteSingleBigEndian(four, single);
                    Bytes.AddRange(four.ToArray());
                    value = (double)single;
                    break;

                case Usage.Double:
                    double number = (random.NextDouble() - 0.5) * 1e12;
                    Span<byte> eight = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleBigEndian(eight, number);
                    Bytes.AddRange(eight.ToArray());
                    value = number;
                    break;

                default:
                    value = Number(node);
                    break;
            }

            if (!node.Filler)
            {
                Expected.Add(new Expected(node, subscripts, _wild ? null : value, _wild, Missing: false));
            }
        }

        private object Number(Node node)
        {
            int[] digits = new int[node.Length];
            if (node.CounterFor is not null)
            {
                string count = counts[node].ToString(CultureInfo.InvariantCulture).PadLeft(node.Length, '0');
                for (int i = 0; i < digits.Length; i++)
                {
                    digits[i] = count[i] - '0';
                }
            }
            else if (random.Next(10) != 0)
            {
                for (int i = 0; i < digits.Length; i++)
                {
                    digits[i] = random.Next(10);
                }
            }

            bool negative = node.Signed && random.Next(2) == 0;
            switch (node.Usage)
            {
                case Usage.Zoned:
                    Zoned(node, digits, negative);
                    break;
                case Usage.Packed:
                    Packed(node, digits, negative);
                    break;
                default:
                    Binary(node, digits, negative);
                    break;
            }

            string all = string.Concat(digits);
            string whole = all[..(node.Length - node.Scale)];
            string fraction = all[(node.Length - node.Scale)..];
            decimal value = decimal.Parse(
                (negative ? "-" : string.Empty) + (whole.Length == 0 ? "0" : whole) + (fraction.Length == 0 ? string.Empty : "." + fraction),
                CultureInfo.InvariantCulture);

            bool unsignedDoubleword = node.Usage == Usage.NativeBinary && !node.Signed && node.Length > 9;
            return node.Scale == 0 && node.Length <= 18 && !unsignedDoubleword ? (object)(long)value : value;
        }

        private void Zoned(Node node, int[] digits, bool negative)
        {
            bool separate = node.Signed && node.Sign is SignKind.LeadingSeparate or SignKind.TrailingSeparate;
            byte sign = ascii ? (negative ? (byte)'-' : (byte)'+') : (negative ? (byte)0x60 : (byte)0x4E);
            if (separate && node.Sign == SignKind.LeadingSeparate)
            {
                Bytes.Add(sign);
            }

            int signAt = !node.Signed || separate ? -1 : node.Sign == SignKind.Leading ? 0 : digits.Length - 1;
            for (int i = 0; i < digits.Length; i++)
            {
                int digit = digits[i];
                if (ascii)
                {
                    char plain = (char)('0' + digit);
                    Bytes.Add((byte)(i != signAt ? plain : negative ? "}JKLMNOPQR"[digit] : random.Next(2) == 0 ? plain : "{ABCDEFGHI"[digit]));
                }
                else
                {
                    int zone = i != signAt ? 0xF : negative ? 0xD : random.Next(2) == 0 ? 0xC : 0xF;
                    Bytes.Add((byte)((zone << 4) | digit));
                }
            }

            if (separate && node.Sign == SignKind.TrailingSeparate)
            {
                Bytes.Add(sign);
            }
        }

        private void Packed(Node node, int[] digits, bool negative)
        {
            var halves = new List<int>(digits);
            if (halves.Count % 2 == 0)
            {
                halves.Insert(0, 0);
            }

            halves.Add(negative ? 0xD : node.Signed ? 0xC : 0xF);
            for (int i = 0; i < halves.Count; i += 2)
            {
                Bytes.Add((byte)((halves[i] << 4) | halves[i + 1]));
            }
        }

        private void Binary(Node node, int[] digits, bool negative)
        {
            long value = long.Parse(string.Concat(digits), CultureInfo.InvariantCulture);
            if (negative)
            {
                value = -value;
            }

            int size = node.Length <= 4 ? 2 : node.Length <= 9 ? 4 : 8;
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            Bytes.AddRange(bytes[(8 - size)..].ToArray());
        }
    }
}

internal sealed class FuzzStatistics
{
    public long FieldsCompared { get; set; }

    public long RecordsCompared { get; set; }

    public long WithVariableTables { get; set; }

    public long WithRedefines { get; set; }

    public long Ascii { get; set; }

    public long DataReads { get; set; }

    public long DataRejected { get; set; }

    public long CopybooksRejected { get; set; }

    public long DamagedCopybooksParsed { get; set; }

    /// <summary>Fields compared, by the generator's usage: text, zoned, packed, binary, native binary, float, double.</summary>
    public long[] Usages { get; } = new long[7];

    public override string ToString() =>
        $"{RecordsCompared} records and {FieldsCompared} fields compared (by usage: {string.Join(", ", Usages)}); " +
        $"{WithVariableTables} copybooks with variable tables, {WithRedefines} with REDEFINES, {Ascii} in ASCII; " +
        $"{DataReads} reads of damaged data, {DataRejected} rejected; " +
        $"{CopybooksRejected} damaged copybooks rejected, {DamagedCopybooksParsed} still parsed and read.";
}
