using CopybookKit.Parsing;

namespace CopybookKit;

/// <summary>How the columns of copybook source are laid out.</summary>
public enum SourceFormat
{
    /// <summary>Decide from the source: fixed unless a line has code in column 7 or before.</summary>
    Auto,

    /// <summary>Columns 1 to 6 are sequence numbers, 7 is the indicator, 8 to 72 are code.</summary>
    Fixed,

    /// <summary>Code may start in any column, and <c>*&gt;</c> starts a comment.</summary>
    Free,
}

/// <summary>A parsed COBOL copybook: one or more record layouts.</summary>
public sealed class Copybook
{
    internal const int MaxSlots = 1_000_000;

    private readonly Dictionary<string, List<DataItem>> _byName = new(StringComparer.OrdinalIgnoreCase);

    internal Copybook(IReadOnlyList<DataItem> records, IReadOnlyList<DataItem> items, int slotCount)
    {
        Records = records;
        Items = items;
        SlotCount = slotCount;
        foreach (DataItem item in items)
        {
            if (item.IsFiller)
            {
                continue;
            }

            if (!_byName.TryGetValue(item.Name, out List<DataItem>? sameName))
            {
                _byName[item.Name] = sameName = [];
            }

            sameName.Add(item);
        }
    }

    /// <summary>The 01-level records, in copybook order.</summary>
    public IReadOnlyList<DataItem> Records { get; }

    /// <summary>Every item in the copybook: each record followed by its subordinate items, in copybook order.</summary>
    public IReadOnlyList<DataItem> Items { get; }

    /// <summary>The size in bytes of the largest record.</summary>
    public int Length => Records.Max(record => record.Size);

    internal int SlotCount { get; }

    /// <summary>Parses copybook source text.</summary>
    /// <exception cref="CopybookException">The copybook is malformed or uses an unsupported clause.</exception>
    public static Copybook Parse(string source, SourceFormat format = SourceFormat.Auto) =>
        CopybookParser.Parse(source, format);

    /// <summary>Reads and parses a copybook file.</summary>
    /// <exception cref="CopybookException">The copybook is malformed or uses an unsupported clause.</exception>
    public static Copybook Load(string path, SourceFormat format = SourceFormat.Auto) =>
        Parse(File.ReadAllText(path), format);

    /// <summary>
    /// Finds an item by name. Names are not case sensitive. When several items share a
    /// name, qualify it with any of its groups, outermost first, joined with dots:
    /// <c>CUSTOMER.ADDRESS.CITY</c> or just <c>ADDRESS.CITY</c>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No item matches.</exception>
    /// <exception cref="ArgumentException">More than one item matches.</exception>
    public DataItem Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string[] parts = name.Split('.', StringSplitOptions.TrimEntries);
        if (!_byName.TryGetValue(parts[^1], out List<DataItem>? candidates))
        {
            throw new KeyNotFoundException($"The copybook has no item named {parts[^1]}.");
        }

        DataItem? found = null;
        foreach (DataItem candidate in candidates)
        {
            if (!IsQualifiedBy(candidate, parts))
            {
                continue;
            }

            if (found is not null)
            {
                throw new ArgumentException(
                    $"The name {name} matches both {found.Path} and {candidate.Path}. Qualify it with a group name.");
            }

            found = candidate;
        }

        return found ?? throw new KeyNotFoundException($"The copybook has no item named {name}.");
    }

    /// <summary>Finds an item by name, or returns false when none or several match.</summary>
    public bool TryFind(string name, out DataItem? item)
    {
        try
        {
            item = Find(name);
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            item = null;
            return false;
        }
    }

    /// <summary>The qualifiers must name groups the item is inside, in order, but may skip levels as COBOL's OF does.</summary>
    private static bool IsQualifiedBy(DataItem item, string[] parts)
    {
        int part = parts.Length - 2;
        for (DataItem? group = item.Parent; group is not null && part >= 0; group = group.Parent)
        {
            if (group.Name.Equals(parts[part], StringComparison.OrdinalIgnoreCase))
            {
                part--;
            }
        }

        return part < 0;
    }
}
