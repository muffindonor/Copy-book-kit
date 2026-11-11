using System.Text.Json;

namespace CopybookKit;

/// <summary>How a data item is stored in the record.</summary>
public enum ItemKind
{
    /// <summary>An item with subordinate items and no storage of its own.</summary>
    Group,

    /// <summary>Character data: <c>PIC X</c>, <c>PIC A</c> and edited pictures such as <c>ZZ9.99</c>.</summary>
    Text,

    /// <summary>A number stored one digit per byte (<c>USAGE DISPLAY</c>).</summary>
    ZonedDecimal,

    /// <summary>A number stored two digits per byte with a sign nibble (<c>COMP-3</c>).</summary>
    PackedDecimal,

    /// <summary>A binary integer of 2, 4 or 8 bytes (<c>COMP</c>, <c>BINARY</c>, <c>COMP-5</c>).</summary>
    Binary,

    /// <summary>A 4-byte floating point number (<c>COMP-1</c>).</summary>
    Float,

    /// <summary>An 8-byte floating point number (<c>COMP-2</c>).</summary>
    Double,
}

/// <summary>Where a zoned decimal item keeps its sign.</summary>
public enum SignPosition
{
    /// <summary>The item is unsigned.</summary>
    None,

    /// <summary>The sign shares the last digit's byte. This is the default for <c>PIC S9</c>.</summary>
    TrailingEmbedded,

    /// <summary>The sign shares the first digit's byte (<c>SIGN LEADING</c>).</summary>
    LeadingEmbedded,

    /// <summary>The sign is its own byte after the digits (<c>SIGN TRAILING SEPARATE</c>).</summary>
    TrailingSeparate,

    /// <summary>The sign is its own byte before the digits (<c>SIGN LEADING SEPARATE</c>).</summary>
    LeadingSeparate,
}

internal enum ValueType
{
    None,
    Text,
    Int64,
    Decimal,
    Double,
}

/// <summary>One data description entry from a copybook: a record, a group or a field.</summary>
public sealed class DataItem
{
    private static readonly IReadOnlyList<DataItem> NoItems = [];

    internal DataItem(string name, int level, int line, bool isFiller)
    {
        Name = name;
        Level = level;
        Line = line;
        IsFiller = isFiller;
        JsonName = JsonEncodedText.Encode(name);
    }

    /// <summary>The data name, or <c>FILLER</c>.</summary>
    public string Name { get; }

    /// <summary>The level number, 01 to 49.</summary>
    public int Level { get; }

    /// <summary>The 1-based copybook line the item starts on.</summary>
    public int Line { get; }

    /// <summary>True for <c>FILLER</c> and for items with no name.</summary>
    public bool IsFiller { get; }

    /// <summary>The group this item belongs to, or null for a record.</summary>
    public DataItem? Parent { get; internal set; }

    /// <summary>The subordinate items, in copybook order. Empty for a field.</summary>
    public IReadOnlyList<DataItem> Children { get; internal set; } = NoItems;

    /// <summary>How the item is stored.</summary>
    public ItemKind Kind { get; internal set; }

    /// <summary>True when the item has subordinate items.</summary>
    public bool IsGroup => Kind == ItemKind.Group;

    /// <summary>True for every kind except <see cref="ItemKind.Group"/> and <see cref="ItemKind.Text"/>.</summary>
    public bool IsNumeric => Kind is not (ItemKind.Group or ItemKind.Text);

    /// <summary>The picture string as written, or null when the item has none.</summary>
    public string? Picture { get; internal set; }

    /// <summary>The size in bytes of one occurrence.</summary>
    public int Size { get; internal set; }

    /// <summary>
    /// The offset in bytes of the first occurrence from the start of the record. When
    /// <see cref="IsVariablyLocated"/> is true, this is the offset with every earlier
    /// <c>OCCURS DEPENDING ON</c> table at its maximum size.
    /// </summary>
    public int Offset { get; internal set; }

    /// <summary>True when the item follows an <c>OCCURS DEPENDING ON</c> table, so its real offset varies by record.</summary>
    public bool IsVariablyLocated { get; internal set; }

    /// <summary>The number of digits stored, for numeric items with a picture.</summary>
    public int Digits { get; internal set; }

    /// <summary>
    /// The number of digits after the decimal point. Negative when the picture scales
    /// the value up with trailing <c>P</c> symbols.
    /// </summary>
    public int Scale { get; internal set; }

    /// <summary>True when the picture starts with <c>S</c>.</summary>
    public bool IsSigned { get; internal set; }

    /// <summary>Where a zoned decimal item keeps its sign.</summary>
    public SignPosition Sign { get; internal set; }

    /// <summary>True for <c>COMP-5</c>, which can hold the full range of its bytes.</summary>
    public bool IsNativeBinary { get; internal set; }

    /// <summary>True when the item has an <c>OCCURS</c> clause.</summary>
    public bool HasOccurs { get; internal set; }

    /// <summary>The smallest number of occurrences. Equal to <see cref="MaxOccurs"/> unless the table is variable.</summary>
    public int MinOccurs { get; internal set; } = 1;

    /// <summary>The largest number of occurrences, or 1 when the item has no <c>OCCURS</c> clause.</summary>
    public int MaxOccurs { get; internal set; } = 1;

    /// <summary>The field that holds the number of occurrences, for <c>OCCURS DEPENDING ON</c>.</summary>
    public DataItem? DependingOn { get; internal set; }

    /// <summary>The item whose storage this item redefines, or null.</summary>
    public DataItem? Redefines { get; internal set; }

    /// <summary>
    /// The items that share this item's storage, including this item. Null when the item
    /// neither redefines nor is redefined. Records in a copybook with several 01 levels
    /// are alternatives of each other.
    /// </summary>
    public IReadOnlyList<DataItem>? Alternatives { get; internal set; }

    /// <summary>The .NET type values of this field are returned as, or null for a group.</summary>
    public Type? ClrType => ValueType switch
    {
        ValueType.Text => typeof(string),
        ValueType.Int64 => typeof(long),
        ValueType.Decimal => typeof(decimal),
        ValueType.Double => typeof(double),
        _ => null,
    };

    /// <summary>The names from the record down to this item, joined with dots. FILLER groups are left out.</summary>
    public string Path
    {
        get
        {
            var names = new List<string>();
            for (DataItem? item = this; item is not null; item = item.Parent)
            {
                if (!item.IsFiller || item == this)
                {
                    names.Add(item.Name);
                }
            }

            names.Reverse();
            return string.Join('.', names);
        }
    }

    internal ValueType ValueType { get; set; }

    /// <summary>Position in a walk of the whole copybook, used to index per-record state.</summary>
    internal int Ordinal { get; set; }

    /// <summary>Slot of the first occurrence's value in a flat record. See <see cref="SlotsPerOccurrence"/>.</summary>
    internal int FirstSlot { get; set; }

    /// <summary>Slots one occurrence of this item uses. Occurrence i starts i times this after the first.</summary>
    internal int SlotsPerOccurrence { get; set; }

    internal bool IsOdoObject { get; set; }

    internal JsonEncodedText JsonName { get; set; }

    /// <summary>This item and every enclosing item that has OCCURS, outermost first.</summary>
    internal DataItem[] OccursChain { get; set; } = [];

    internal DataItem Record
    {
        get
        {
            DataItem item = this;
            while (item.Parent is not null)
            {
                item = item.Parent;
            }

            return item;
        }
    }

    /// <summary>Returns the level, name and storage, for example <c>05 CUST-BAL PackedDecimal(7)</c>.</summary>
    public override string ToString() => $"{Level:00} {Name} {Kind}({Size})";
}
