namespace CopybookKit;

/// <summary>A copybook is malformed, or uses a clause this library doesn't support.</summary>
public sealed class CopybookException : Exception
{
    /// <summary>Creates the exception for a 1-based source line, or 0 when no line applies.</summary>
    public CopybookException(string message, int line)
        : base(line > 0 ? $"Copybook line {line}: {message}" : $"Copybook: {message}")
    {
        Line = line;
    }

    /// <summary>The 1-based copybook line the problem was found on, or 0.</summary>
    public int Line { get; }
}

/// <summary>A data file doesn't match its copybook or its record format.</summary>
public sealed class RecordFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RecordFormatException(string message)
        : base(message)
    {
    }
}
