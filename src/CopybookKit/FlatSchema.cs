namespace CopybookKit;

/// <summary>One column of a record laid out flat: a field, or one occurrence of a field in a table.</summary>
public sealed class FlatColumn
{
    internal FlatColumn(string name, DataItem item, int[] subscripts, int slot)
    {
        Name = name;
        Item = item;
        Subscripts = subscripts;
        Slot = slot;
    }

    /// <summary>
    /// The column name: the field name, followed by 1-based subscripts in parentheses for a
    /// field in a table, such as <c>PHONE(2)</c>. A name used by several fields is
    /// qualified with its groups, such as <c>HOME.CITY</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>The field the column holds.</summary>
    public DataItem Item { get; }

    /// <summary>The 1-based subscripts of this occurrence, outermost table first. Empty for a field outside any table.</summary>
    public IReadOnlyList<int> Subscripts { get; }

    /// <summary>The .NET type of the column's values: string, long, decimal or double.</summary>
    public Type ClrType => Item.ClrType!;

    internal int Slot { get; }
}

/// <summary>
/// The columns of a record laid out flat, for CSV and other tabular output. Tables are
/// expanded to their largest size, and every REDEFINES alternative gets its own columns.
/// </summary>
public sealed class FlatSchema
{
    internal FlatSchema(IReadOnlyList<DataItem> records)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DataItem record in records)
        {
            CountNames(record, counts);
        }

        var columns = new List<FlatColumn>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataItem record in records)
        {
            AddColumns(record, 0, [], counts, records.Count > 1, used, columns);
        }

        Columns = columns;
    }

    /// <summary>The columns, in copybook order.</summary>
    public IReadOnlyList<FlatColumn> Columns { get; }

    /// <summary>Returns the position of the column with this name, or -1.</summary>
    public int IndexOf(string columnName)
    {
        for (int i = 0; i < Columns.Count; i++)
        {
            if (Columns[i].Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static void CountNames(DataItem item, Dictionary<string, int> counts)
    {
        if (!item.IsGroup && !item.IsFiller)
        {
            counts[item.Name] = counts.GetValueOrDefault(item.Name) + 1;
        }

        foreach (DataItem child in item.Children)
        {
            CountNames(child, counts);
        }
    }

    private static void AddColumns(
        DataItem item,
        int slotShift,
        List<int> subscripts,
        Dictionary<string, int> counts,
        bool includeRecordName,
        HashSet<string> used,
        List<FlatColumn> columns)
    {
        for (int i = 0; i < item.MaxOccurs; i++)
        {
            int shift = slotShift + (i * item.SlotsPerOccurrence);
            if (item.HasOccurs)
            {
                subscripts.Add(i + 1);
            }

            if (item.IsGroup)
            {
                foreach (DataItem child in item.Children)
                {
                    AddColumns(child, shift, subscripts, counts, includeRecordName, used, columns);
                }
            }
            else if (!item.IsFiller)
            {
                string name = counts[item.Name] == 1 ? item.Name : Qualify(item, includeRecordName);
                if (subscripts.Count > 0)
                {
                    name = $"{name}({string.Join(',', subscripts)})";
                }

                string unique = name;
                for (int n = 2; !used.Add(unique); n++)
                {
                    unique = $"{name}#{n}";
                }

                columns.Add(new FlatColumn(unique, item, [.. subscripts], item.FirstSlot + shift));
            }

            if (item.HasOccurs)
            {
                subscripts.RemoveAt(subscripts.Count - 1);
            }
        }
    }

    private static string Qualify(DataItem item, bool includeRecordName)
    {
        string path = item.Path;
        if (!includeRecordName && item.Parent is not null)
        {
            int dot = path.IndexOf('.');
            DataItem record = item.Record;
            if (dot >= 0 && !record.IsFiller)
            {
                path = path[(dot + 1)..];
            }
        }

        return path;
    }
}
