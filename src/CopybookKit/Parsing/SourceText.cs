using System.Text;

namespace CopybookKit.Parsing;

/// <summary>
/// Strips a copybook down to its code: sequence numbers, the indicator column, the
/// identification area and comment lines are removed. Every source line still maps to
/// one output line, so that errors can name the right line.
/// </summary>
internal static class SourceText
{
    /// <summary>Marks an output line as a continuation of the line before it.</summary>
    public const char Continuation = '\u0001';

    private const int IndicatorColumn = 6;
    private const int FirstCodeColumn = 7;
    private const int LastCodeColumn = 72;
    private const int LastColumn = 80;

    public static string Prepare(string source, SourceFormat format)
    {
        string[] lines = source.Replace("\u001A", string.Empty).ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = RemoveControlCharacters(ExpandTabs(lines[i]));
        }

        bool isFixed = format switch
        {
            SourceFormat.Fixed => true,
            SourceFormat.Free => false,
            _ => LooksFixed(lines),
        };

        var code = new StringBuilder(source.Length);
        foreach (string line in lines)
        {
            if (isFixed)
            {
                AppendFixed(line, code);
            }
            else if (!line.AsSpan().TrimStart().StartsWith("*"))
            {
                code.Append(line);
            }

            code.Append('\n');
        }

        return code.ToString();
    }

    private static void AppendFixed(string line, StringBuilder code)
    {
        if (line.Length <= IndicatorColumn)
        {
            return;
        }

        char indicator = line[IndicatorColumn];
        if (indicator is '*' or '/' or 'D' or 'd' or '$')
        {
            return;
        }

        if (indicator == '-')
        {
            code.Append(Continuation);
        }

        if (line.Length > FirstCodeColumn)
        {
            code.Append(line, FirstCodeColumn, Math.Min(line.Length, LastCodeColumn) - FirstCodeColumn);
        }
    }

    /// <summary>
    /// Fixed format keeps columns 1 to 6 for a sequence number and column 7 for an
    /// indicator. Code in column 7, or a sequence area that is only partly filled as
    /// lightly indented free-format code would leave it, means free format. So does a
    /// line longer than 80 columns.
    /// </summary>
    private static bool LooksFixed(string[] lines)
    {
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Cutting a longer line at column 72 would silently drop clauses.
            if (line.AsSpan().TrimEnd().Length > LastColumn)
            {
                return false;
            }

            ReadOnlySpan<char> sequence = line.AsSpan(0, Math.Min(line.Length, IndicatorColumn));
            if (!sequence.IsWhiteSpace() && sequence.Contains(' '))
            {
                return false;
            }

            if (line.Length <= IndicatorColumn)
            {
                if (sequence.ContainsAnyExcept("0123456789"))
                {
                    return false;
                }

                continue;
            }

            if (line[IndicatorColumn] is not (' ' or '*' or '/' or '-' or 'D' or 'd' or '$'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Control characters have no meaning in COBOL source. Turning them into spaces keeps
    /// escape sequences out of item names and error messages, which end up on a terminal.
    /// </summary>
    private static string RemoveControlCharacters(string line)
    {
        if (!line.AsSpan().ContainsAnyInRange('\u0000', '\u001F') && !line.AsSpan().ContainsAnyInRange('\u007F', '\u009F'))
        {
            return line;
        }

        return string.Create(line.Length, line, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
    }

    private static string ExpandTabs(string line)
    {
        if (!line.Contains('\t'))
        {
            return line;
        }

        var expanded = new StringBuilder(line.Length + 16);
        foreach (char c in line)
        {
            if (c == '\t')
            {
                expanded.Append(' ', 8 - (expanded.Length % 8));
            }
            else
            {
                expanded.Append(c);
            }
        }

        return expanded.ToString();
    }
}
