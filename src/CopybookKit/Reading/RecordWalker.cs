using CopybookKit.Decoding;

namespace CopybookKit.Reading;

/// <summary>Receives the items of one record in copybook order.</summary>
internal interface IRecordVisitor
{
    /// <summary>Starts the occurrences of an item that has OCCURS.</summary>
    void BeginArray(DataItem item);

    void EndArray(DataItem item);

    /// <summary>Starts a group, or one occurrence of a group that has OCCURS.</summary>
    void BeginGroup(DataItem item);

    void EndGroup(DataItem item);

    /// <summary>
    /// A field, or one occurrence of a field that has OCCURS. <paramref name="present"/> is
    /// false when the record ends before the field does.
    /// </summary>
    void Field(DataItem item, ReadOnlySpan<byte> bytes, bool present, int slot);
}

/// <summary>
/// Walks a record against its layout and hands each field's bytes to a visitor. Positions
/// are worked out as it goes, because an OCCURS DEPENDING ON table moves everything after it.
/// </summary>
internal sealed class RecordWalker(Copybook copybook, ValueDecoder decoder)
{
    private const long NotRead = long.MinValue;

    /// <summary>Where each item started in the current record, so that a REDEFINES can start there too.</summary>
    private readonly int[] _starts = new int[copybook.Items.Count];

    /// <summary>The values of the fields that OCCURS DEPENDING ON clauses name, by item ordinal.</summary>
    private readonly long[] _counters = new long[copybook.Items.Count];

    private readonly DataItem[] _counterItems = [.. copybook.Items.Where(item => item.IsOdoObject)];

    /// <summary>Alternatives a selector ruled out for the current record, by item ordinal.</summary>
    public bool[] Inactive { get; } = new bool[copybook.Items.Count];

    public void Walk(DataItem record, ReadOnlySpan<byte> data, IRecordVisitor visitor)
    {
        foreach (DataItem counter in _counterItems)
        {
            _counters[counter.Ordinal] = NotRead;
        }

        if (record.IsGroup)
        {
            WalkChildren(record, data, 0, 0, visitor);
        }
        else
        {
            WalkItem(record, data, 0, 0, visitor);
        }
    }

    /// <summary>Returns the position after the last child.</summary>
    private int WalkChildren(DataItem group, ReadOnlySpan<byte> data, int start, int slotShift, IRecordVisitor visitor)
    {
        int position = start;
        foreach (DataItem child in group.Children)
        {
            int childStart = child.Redefines is { } redefined ? _starts[redefined.Ordinal] : position;
            _starts[child.Ordinal] = childStart;
            if (Inactive[child.Ordinal])
            {
                // The storage is still there. Only the redefined item says how much.
                if (child.Redefines is null)
                {
                    position = Math.Max(position, childStart + (child.Size * child.MaxOccurs));
                }

                continue;
            }

            position = Math.Max(position, WalkItem(child, data, childStart, slotShift, visitor));
        }

        return position;
    }

    /// <summary>Returns the position after the item's last occurrence.</summary>
    private int WalkItem(DataItem item, ReadOnlySpan<byte> data, int start, int slotShift, IRecordVisitor visitor)
    {
        int count = item.DependingOn is { } counter ? ReadCount(item, counter) : item.MaxOccurs;
        bool visible = item.IsGroup || !item.IsFiller;
        if (!visible)
        {
            // FILLER has no value to read, so its occurrences are stepped over in one go.
            return start + (item.Size * count);
        }

        if (item.HasOccurs && visible)
        {
            visitor.BeginArray(item);
        }

        int position = start;
        for (int i = 0; i < count; i++)
        {
            int shift = slotShift + (i * item.SlotsPerOccurrence);
            if (item.IsGroup)
            {
                visitor.BeginGroup(item);
                position = WalkChildren(item, data, position, shift, visitor);
                visitor.EndGroup(item);
                continue;
            }

            if (visible)
            {
                bool present = position + item.Size <= data.Length;
                ReadOnlySpan<byte> bytes = present ? data.Slice(position, item.Size) : default;
                if (item.IsOdoObject)
                {
                    _counters[item.Ordinal] =
                        present && decoder.TryDecode(item, bytes, out FieldValue value) ? value.Int64 : NotRead;
                }

                visitor.Field(item, bytes, present, item.FirstSlot + shift);
            }

            position += item.Size;
        }

        if (item.HasOccurs && visible)
        {
            visitor.EndArray(item);
        }

        return position;
    }

    private int ReadCount(DataItem table, DataItem counter)
    {
        long count = _counters[counter.Ordinal];
        if (count == NotRead)
        {
            throw new RecordFormatException(
                $"Record {decoder.RecordNumber}: {table.Name} depends on {counter.Name}, which has no valid value in this record." +
                SelectorHint(table));
        }

        if (count < 0 || count > table.MaxOccurs)
        {
            throw new RecordFormatException(
                $"Record {decoder.RecordNumber}: {table.Name} depends on {counter.Name}, which is {count}. " +
                $"The copybook allows 0 to {table.MaxOccurs}." + SelectorHint(table));
        }

        return (int)count;
    }

    /// <summary>
    /// A table inside a REDEFINES alternative is read for every record unless a selector
    /// rules the alternative out, and in a record of another type its counter is garbage.
    /// </summary>
    private static string SelectorHint(DataItem table)
    {
        for (DataItem? item = table; item is not null; item = item.Parent)
        {
            if (item.Alternatives is not null && item.Parent is not null)
            {
                return $" {item.Name} is one of several layouts for the same bytes: if the file holds several record types, " +
                    "choose the layout for each record with a selector (--select in the command-line tool).";
            }
        }

        return string.Empty;
    }
}
