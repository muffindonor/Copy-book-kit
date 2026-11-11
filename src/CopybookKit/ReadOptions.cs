using System.Text;

namespace CopybookKit;

/// <summary>How records are separated in a data file.</summary>
public enum RecordFormat
{
    /// <summary>Every record has the same length and nothing separates them (RECFM=F or FB).</summary>
    Fixed,

    /// <summary>
    /// Each record starts with a 4-byte record descriptor word holding its length (RECFM=V or
    /// VB, transferred with the descriptor words kept).
    /// </summary>
    VariableRdw,

    /// <summary>A text file with one record per line, as PC COBOL's LINE SEQUENTIAL writes.</summary>
    LineSequential,
}

/// <summary>What to do with a numeric field whose bytes are not a valid number.</summary>
public enum InvalidDataPolicy
{
    /// <summary>Return null for the field and count it in <see cref="ReadStatistics.InvalidFields"/>.</summary>
    Null,

    /// <summary>Throw a <see cref="RecordFormatException"/> that names the record and field.</summary>
    Throw,
}

/// <summary>Which spaces to remove from text fields.</summary>
public enum TextTrim
{
    /// <summary>Remove trailing spaces. COBOL pads every text field with them.</summary>
    Right,

    /// <summary>Remove leading and trailing spaces.</summary>
    Both,

    /// <summary>Keep the field exactly as stored.</summary>
    None,
}

/// <summary>The byte order of binary fields.</summary>
public enum ByteOrder
{
    /// <summary>Most significant byte first, as on a mainframe.</summary>
    BigEndian,

    /// <summary>Least significant byte first, as COMP-5 on an x86 machine.</summary>
    LittleEndian,
}

/// <summary>How COMP-1 and COMP-2 fields are stored.</summary>
public enum FloatFormat
{
    /// <summary>IBM hexadecimal floating point, the mainframe default.</summary>
    IbmHex,

    /// <summary>IEEE 754 binary floating point.</summary>
    Ieee,
}

/// <summary>
/// Picks which layout applies to each record. The value of <see cref="Field"/> is looked
/// up in <see cref="Cases"/>, and the item it names becomes the only one of its
/// alternatives that is decoded for that record.
/// </summary>
/// <param name="Field">
/// The field whose value identifies the record type. It must have a fixed position in the record.
/// </param>
/// <param name="Cases">
/// Maps a field value to the name of a record, of an item with REDEFINES, or of the item
/// that is redefined. Text values are compared after trimming, and numbers in their
/// plain form, such as <c>2</c> or <c>-1.50</c>.
/// </param>
public sealed record RedefineRule(string Field, IReadOnlyDictionary<string, string> Cases);

/// <summary>Describes a data file: its character set, its record format and how to treat bad data.</summary>
public sealed class ReadOptions
{
    /// <summary>
    /// The character set of the file. Defaults to EBCDIC code page 037. Use
    /// <see cref="EbcdicEncodings"/> for other EBCDIC code pages, or
    /// <see cref="System.Text.Encoding.ASCII"/> for a file converted to ASCII.
    /// </summary>
    public Encoding Encoding { get; set; } = EbcdicEncodings.Cp037;

    /// <summary>How records are separated. Defaults to <see cref="RecordFormat.Fixed"/>.</summary>
    public RecordFormat Format { get; set; } = RecordFormat.Fixed;

    /// <summary>
    /// The record length in bytes for <see cref="RecordFormat.Fixed"/>. Defaults to the
    /// length of the largest record in the copybook.
    /// </summary>
    public int? RecordLength { get; set; }

    /// <summary>The 01-level record to read when the copybook has several. Defaults to the first.</summary>
    public string? RecordName { get; set; }

    /// <summary>
    /// Rules that pick one layout per record where the copybook has alternatives. Without
    /// a rule, every REDEFINES alternative is decoded for every record.
    /// </summary>
    public IList<RedefineRule> Selectors { get; } = [];

    /// <summary>What to do with invalid numeric data. Defaults to <see cref="InvalidDataPolicy.Null"/>.</summary>
    public InvalidDataPolicy InvalidData { get; set; } = InvalidDataPolicy.Null;

    /// <summary>Which spaces to remove from text fields. Defaults to <see cref="TextTrim.Right"/>.</summary>
    public TextTrim Trim { get; set; } = TextTrim.Right;

    /// <summary>The byte order of binary fields. Defaults to <see cref="ByteOrder.BigEndian"/>.</summary>
    public ByteOrder BinaryByteOrder { get; set; } = ByteOrder.BigEndian;

    /// <summary>
    /// How COMP-1 and COMP-2 fields are stored. Defaults to IBM hexadecimal for an EBCDIC
    /// file and IEEE 754 otherwise.
    /// </summary>
    public FloatFormat? FloatFormat { get; set; }
}

/// <summary>Counts of what a <see cref="CopybookReader"/> has read so far.</summary>
public sealed class ReadStatistics
{
    internal const int MaxIssues = 20;

    private readonly List<string> _issues = [];

    /// <summary>Records read.</summary>
    public long Records { get; internal set; }

    /// <summary>Numeric fields that held only spaces or zero bytes, and were returned as null.</summary>
    public long BlankNumericFields { get; internal set; }

    /// <summary>Numeric fields whose bytes were not a valid number, and were returned as null.</summary>
    public long InvalidFields { get; internal set; }

    /// <summary>Fields that lay beyond the end of a short record, and were returned as null.</summary>
    public long MissingFields { get; internal set; }

    /// <summary>Records whose selector field had a value that no rule listed.</summary>
    public long UnmatchedRecords { get; internal set; }

    /// <summary>A description of the first few invalid fields, for diagnosing a wrong copybook or code page.</summary>
    public IReadOnlyList<string> Issues => _issues;

    internal void AddIssue(string issue)
    {
        if (_issues.Count < MaxIssues)
        {
            _issues.Add(issue);
        }
    }
}
