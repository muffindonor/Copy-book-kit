using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CopybookKit.Decoding;
using CopybookKit.Reading;

namespace CopybookKit;

/// <summary>
/// Reads a mainframe data file one record at a time and decodes its fields with a copybook.
/// Call <see cref="Read"/> to move to the next record, then read fields by name, take the
/// whole record as flat values, or write it as JSON.
/// </summary>
public sealed class CopybookReader : IDisposable
{
    private const int FileBufferSize = 1 << 16;

    private readonly RecordSource _source;
    private readonly ValueDecoder _decoder;
    private readonly RecordWalker _walker;
    private readonly SelectorRule[] _rules;
    private readonly DataItem _defaultRecord;
    private readonly object?[] _slots;
    private readonly FlatVisitor _flatVisitor;
    private readonly JsonVisitor _jsonVisitor;
    private object?[]? _row;
    private FlatSchema? _schema;
    private ReadOnlyMemory<byte> _record;
    private bool _hasRecord;
    private bool _slotsReady;
    private bool _counted;

    /// <summary>Creates a reader over a stream.</summary>
    /// <param name="stream">The data file.</param>
    /// <param name="copybook">The layout of its records.</param>
    /// <param name="options">The file's character set and record format. Defaults to fixed-length EBCDIC code page 037.</param>
    /// <param name="leaveOpen">True to leave the stream open when the reader is disposed.</param>
    /// <exception cref="ArgumentException">An option names an item that doesn't exist or can't be used that way.</exception>
    public CopybookReader(Stream stream, Copybook copybook, ReadOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(copybook);
        options ??= new ReadOptions();

        Copybook = copybook;
        Statistics = new ReadStatistics();
        _decoder = new ValueDecoder(options, Statistics);
        _walker = new RecordWalker(copybook, _decoder);
        _slots = new object?[copybook.SlotCount];
        _flatVisitor = new FlatVisitor(_decoder, _slots);
        _jsonVisitor = new JsonVisitor(_decoder);

        _defaultRecord = options.RecordName is null ? copybook.Records[0] : FindRecord(copybook, options.RecordName);
        Record = _defaultRecord;
        _rules = [.. options.Selectors.Select(rule => SelectorRule.Compile(copybook, rule))];

        int recordLength = options.RecordLength ?? copybook.Length;
        _source = options.Format switch
        {
            RecordFormat.Fixed => new FixedRecordSource(stream, recordLength, leaveOpen),
            RecordFormat.VariableRdw => new RdwRecordSource(stream, leaveOpen),
            RecordFormat.LineSequential => new LineRecordSource(stream, recordLength, _decoder.Space, leaveOpen),
            _ => throw new ArgumentException($"Record format {options.Format} is not supported.", nameof(options)),
        };
    }

    /// <summary>The copybook the reader decodes with.</summary>
    public Copybook Copybook { get; }

    /// <summary>Counts of records read and of fields that could not be decoded.</summary>
    public ReadStatistics Statistics { get; }

    /// <summary>The 1-based number of the current record, or 0 before the first <see cref="Read"/>.</summary>
    public long RecordNumber { get; private set; }

    /// <summary>The layout the current record is decoded with. It changes only when a selector picks between records.</summary>
    public DataItem Record { get; private set; }

    /// <summary>The bytes of the current record, exactly as they are in the file.</summary>
    public ReadOnlySpan<byte> RawRecord => _hasRecord ? _record.Span : throw NoRecord();

    /// <summary>
    /// The columns that <see cref="GetValues"/> returns: every field of the record being
    /// read, and of any record a selector can switch to.
    /// </summary>
    public FlatSchema Schema
    {
        get
        {
            if (_schema is null)
            {
                var records = new List<DataItem> { _defaultRecord };
                foreach (SelectorRule rule in _rules)
                {
                    foreach (DataItem target in rule.Cases.Values)
                    {
                        if (target.Parent is null && !records.Contains(target))
                        {
                            records.Add(target);
                        }
                    }
                }

                records.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
                _schema = new FlatSchema(records);
            }

            return _schema;
        }
    }

    /// <summary>Opens a data file.</summary>
    /// <param name="path">The path of the data file.</param>
    /// <param name="copybook">The layout of its records.</param>
    /// <param name="options">The file's character set and record format. Defaults to fixed-length EBCDIC code page 037.</param>
    public static CopybookReader Open(string path, Copybook copybook, ReadOptions? options = null)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize, FileOptions.SequentialScan);
        try
        {
            return new CopybookReader(stream, copybook, options);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Moves to the next record. Returns false at the end of the file.</summary>
    /// <exception cref="RecordFormatException">The file doesn't match the record format.</exception>
    public bool Read()
    {
        _slotsReady = false;
        _counted = false;
        _hasRecord = _source.TryRead(out _record);
        if (!_hasRecord)
        {
            return false;
        }

        RecordNumber++;
        Statistics.Records = RecordNumber;
        _decoder.RecordNumber = RecordNumber;
        SelectLayout();
        return true;
    }

    /// <summary>
    /// Gets a field's value: a string, long, decimal or double, or null when the field is
    /// blank, invalid or beyond the end of the record. A field inside a table takes one
    /// 1-based subscript per enclosing OCCURS, outermost first.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The copybook has no such item.</exception>
    /// <exception cref="ArgumentException">The item is a group, or the subscripts don't fit its tables.</exception>
    public object? GetValue(string name, params int[] subscripts) => GetValue(Copybook.Find(name), subscripts);

    /// <inheritdoc cref="GetValue(string, int[])"/>
    public object? GetValue(DataItem item, params int[] subscripts)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(subscripts);
        if (item.IsGroup || item.IsFiller)
        {
            throw new ArgumentException($"{item.Name} is not a named field, so it has no value of its own.", nameof(item));
        }

        DataItem[] tables = item.OccursChain;
        if (subscripts.Length != tables.Length)
        {
            throw new ArgumentException(
                $"{item.Name} needs {tables.Length} subscript(s), but {subscripts.Length} were given.", nameof(subscripts));
        }

        int slot = item.FirstSlot;
        for (int i = 0; i < tables.Length; i++)
        {
            if (subscripts[i] < 1 || subscripts[i] > tables[i].MaxOccurs)
            {
                throw new ArgumentException(
                    $"Subscript {subscripts[i]} is outside 1 to {tables[i].MaxOccurs} for {tables[i].Name}.", nameof(subscripts));
            }

            slot += (subscripts[i] - 1) * tables[i].SlotsPerOccurrence;
        }

        EnsureSlots();
        return _slots[slot];
    }

    /// <summary>Gets a field as text. Numbers are formatted without regard to the current culture.</summary>
    public string? GetString(string name, params int[] subscripts) =>
        GetValue(name, subscripts) switch
        {
            null => null,
            string text => text,
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            object other => other.ToString(),
        };

    /// <summary>Gets a numeric field as a decimal, or null when it has no value.</summary>
    /// <exception cref="InvalidCastException">The field is text.</exception>
    public decimal? GetDecimal(string name, params int[] subscripts) =>
        GetValue(name, subscripts) switch
        {
            null => null,
            decimal value => value,
            long value => value,
            double value => (decimal)value,
            _ => throw new InvalidCastException($"{name} is a text field."),
        };

    /// <summary>Gets a whole-number field as a long, or null when it has no value.</summary>
    /// <exception cref="InvalidCastException">The field is text, or its value has a fraction.</exception>
    public long? GetInt64(string name, params int[] subscripts) =>
        GetValue(name, subscripts) switch
        {
            null => null,
            long value => value,
            decimal value when value == decimal.Truncate(value) => (long)value,
            decimal => throw new InvalidCastException($"{name} has a fraction."),
            _ => throw new InvalidCastException($"{name} is not a whole-number field."),
        };

    /// <summary>
    /// Gets every field of the current record, in the order of <see cref="Schema"/>. The
    /// list is reused: copy it to keep it past the next <see cref="Read"/>.
    /// </summary>
    public IReadOnlyList<object?> GetValues()
    {
        EnsureSlots();
        IReadOnlyList<FlatColumn> columns = Schema.Columns;
        _row ??= new object?[columns.Count];
        for (int i = 0; i < _row.Length; i++)
        {
            _row[i] = _slots[columns[i].Slot];
        }

        return _row;
    }

    /// <summary>
    /// Writes the current record as one JSON object. Groups become objects and tables
    /// become arrays. FILLER is left out, and so are alternatives a selector ruled out.
    /// </summary>
    public void WriteJson(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ReadOnlySpan<byte> data = RawRecord;
        _decoder.Counting = !_counted;
        _jsonVisitor.Writer = writer;
        writer.WriteStartObject();
        _walker.Walk(Record, data, _jsonVisitor);
        writer.WriteEndObject();
        _counted = true;
    }

    /// <summary>Returns the current record as a JSON object.</summary>
    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteJson(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Closes the data file.</summary>
    public void Dispose() => _source.Dispose();

    private static InvalidOperationException NoRecord() =>
        new("There is no current record. Call Read first, and stop when it returns false.");

    private static DataItem FindRecord(Copybook copybook, string name)
    {
        foreach (DataItem record in copybook.Records)
        {
            if (record.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return record;
            }
        }

        string known = string.Join(", ", copybook.Records.Select(record => record.Name));
        throw new ArgumentException($"The copybook has no record named {name}. Its records are: {known}.");
    }

    private void EnsureSlots()
    {
        if (_slotsReady)
        {
            return;
        }

        ReadOnlySpan<byte> data = RawRecord;
        Array.Clear(_slots);
        _decoder.Counting = !_counted;
        _walker.Walk(Record, data, _flatVisitor);
        _counted = true;
        _slotsReady = true;
    }

    /// <summary>Applies the selector rules to the new record.</summary>
    private void SelectLayout()
    {
        if (_rules.Length == 0)
        {
            return;
        }

        bool[] inactive = _walker.Inactive;
        Array.Clear(inactive);
        Record = _defaultRecord;
        bool unmatched = false;
        ReadOnlySpan<byte> data = _record.Span;

        foreach (SelectorRule rule in _rules)
        {
            DataItem field = rule.Field;
            string? key = null;
            if (field.Offset + field.Size <= data.Length && _decoder.TryDecode(field, data.Slice(field.Offset, field.Size), out FieldValue value))
            {
                key = value.Type switch
                {
                    ValueType.Text => value.Text.Trim().ToString(),
                    ValueType.Int64 => value.Int64.ToString(CultureInfo.InvariantCulture),
                    _ => value.Decimal.ToString(CultureInfo.InvariantCulture),
                };
            }

            if (key is null || !rule.Cases.TryGetValue(key, out DataItem? target))
            {
                unmatched = true;
                continue;
            }

            if (target.Parent is null)
            {
                Record = target;
                continue;
            }

            foreach (DataItem alternative in target.Alternatives!)
            {
                inactive[alternative.Ordinal] = alternative != target;
            }
        }

        if (unmatched)
        {
            Statistics.UnmatchedRecords++;
        }
    }

    private sealed class SelectorRule(DataItem field, Dictionary<string, DataItem> cases)
    {
        public DataItem Field { get; } = field;

        public Dictionary<string, DataItem> Cases { get; } = cases;

        public static SelectorRule Compile(Copybook copybook, RedefineRule rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            DataItem field = Find(copybook, rule.Field);
            if (field.IsGroup || field.Kind is ItemKind.Float or ItemKind.Double)
            {
                throw new ArgumentException($"Selector field {field.Name} must be a text or decimal field.");
            }

            if (field.IsVariablyLocated || field.OccursChain.Length > 0)
            {
                throw new ArgumentException($"Selector field {field.Name} must have a fixed position, outside any table.");
            }

            var cases = new Dictionary<string, DataItem>(StringComparer.Ordinal);
            foreach ((string value, string targetName) in rule.Cases)
            {
                DataItem target = Find(copybook, targetName);
                if (target.Alternatives is null)
                {
                    throw new ArgumentException(
                        $"{target.Name} can't be selected: it has no alternatives. Name a record, an item with REDEFINES, or the item it redefines.");
                }

                cases[value.Trim()] = target;
            }

            return new SelectorRule(field, cases);
        }

        private static DataItem Find(Copybook copybook, string name)
        {
            try
            {
                return copybook.Find(name);
            }
            catch (KeyNotFoundException ex)
            {
                throw new ArgumentException(ex.Message, ex);
            }
        }
    }

    /// <summary>Stores each field's value in its slot.</summary>
    private sealed class FlatVisitor(ValueDecoder decoder, object?[] slots) : IRecordVisitor
    {
        public void BeginArray(DataItem item)
        {
        }

        public void EndArray(DataItem item)
        {
        }

        public void BeginGroup(DataItem item)
        {
        }

        public void EndGroup(DataItem item)
        {
        }

        public void Field(DataItem item, ReadOnlySpan<byte> bytes, bool present, int slot)
        {
            decoder.Decode(item, bytes, present, out FieldValue value);
            slots[slot] = value.Type switch
            {
                ValueType.Text => value.Text.IsEmpty ? string.Empty : new string(value.Text),
                ValueType.Int64 => value.Int64,
                ValueType.Decimal => value.Decimal,
                ValueType.Double => value.Double,
                _ => null,
            };
        }
    }

    /// <summary>Writes each field straight to JSON, without building the record in memory first.</summary>
    private sealed class JsonVisitor(ValueDecoder decoder) : IRecordVisitor
    {
        public Utf8JsonWriter Writer { get; set; } = null!;

        public void BeginArray(DataItem item) => Writer.WriteStartArray(item.JsonName);

        public void EndArray(DataItem item) => Writer.WriteEndArray();

        public void BeginGroup(DataItem item)
        {
            if (item.HasOccurs)
            {
                Writer.WriteStartObject();
            }
            else if (!item.IsFiller)
            {
                Writer.WriteStartObject(item.JsonName);
            }
        }

        public void EndGroup(DataItem item)
        {
            // The fields of a FILLER group that isn't a table are written into the group around it.
            if (item.HasOccurs || !item.IsFiller)
            {
                Writer.WriteEndObject();
            }
        }

        public void Field(DataItem item, ReadOnlySpan<byte> bytes, bool present, int slot)
        {
            decoder.Decode(item, bytes, present, out FieldValue value);
            if (!item.HasOccurs)
            {
                Writer.WritePropertyName(item.JsonName);
            }

            switch (value.Type)
            {
                case ValueType.Text:
                    Writer.WriteStringValue(value.Text);
                    break;
                case ValueType.Int64:
                    Writer.WriteNumberValue(value.Int64);
                    break;
                case ValueType.Decimal:
                    Writer.WriteNumberValue(value.Decimal);
                    break;
                case ValueType.Double when double.IsFinite(value.Double):
                    Writer.WriteNumberValue(value.Double);
                    break;
                default:
                    Writer.WriteNullValue();
                    break;
            }
        }
    }
}
