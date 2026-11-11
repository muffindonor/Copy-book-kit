using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace CopybookKit;

/// <summary>Writes the rest of a data file as JSON Lines or CSV.</summary>
public static class CopybookExport
{
    private static readonly byte[] NewLine = [(byte)'\n'];

    /// <summary>
    /// Writes each remaining record as one JSON object per line. Groups become objects and
    /// tables become arrays.
    /// </summary>
    /// <param name="reader">The reader to take records from.</param>
    /// <param name="output">The stream to write UTF-8 JSON to.</param>
    /// <param name="limit">The most records to write, or null for all of them.</param>
    /// <returns>The number of records written.</returns>
    public static long WriteJsonLines(this CopybookReader reader, Stream output, long? limit = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(output);
        long written = 0;

        // Each record is built in memory first, so one that fails part-way leaves no half line behind.
        var line = new ArrayBufferWriter<byte>(1024);
        using var writer = new Utf8JsonWriter(line);
        while ((limit is null || written < limit) && reader.Read())
        {
            line.ResetWrittenCount();
            writer.Reset();
            reader.WriteJson(writer);
            writer.Flush();
            output.Write(line.WrittenSpan);
            output.Write(NewLine);
            written++;
        }

        return written;
    }

    /// <summary>
    /// Writes a header and each remaining record as one CSV row, with the columns of
    /// <see cref="CopybookReader.Schema"/>. Null values are written as empty cells.
    /// </summary>
    /// <param name="reader">The reader to take records from.</param>
    /// <param name="output">The writer to write CSV to.</param>
    /// <param name="limit">The most records to write, or null for all of them.</param>
    /// <returns>The number of records written.</returns>
    public static long WriteCsv(this CopybookReader reader, TextWriter output, long? limit = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(output);
        IReadOnlyList<FlatColumn> columns = reader.Schema.Columns;
        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                output.Write(',');
            }

            WriteCell(output, columns[i].Name);
        }

        output.Write("\r\n");
        long written = 0;
        while ((limit is null || written < limit) && reader.Read())
        {
            IReadOnlyList<object?> values = reader.GetValues();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    output.Write(',');
                }

                switch (values[i])
                {
                    case null:
                        break;
                    case string text:
                        WriteCell(output, text);
                        break;
                    case double number:
                        output.Write(number.ToString("R", CultureInfo.InvariantCulture));
                        break;
                    case IFormattable number:
                        output.Write(number.ToString(null, CultureInfo.InvariantCulture));
                        break;
                }
            }

            output.Write("\r\n");
            written++;
        }

        return written;
    }

    /// <summary>Quotes a cell when it holds a comma, a quote, a line break or outer spaces (RFC 4180).</summary>
    private static void WriteCell(TextWriter output, string text)
    {
        bool quote = text.AsSpan().ContainsAny(",\"\r\n") || (text.Length > 0 && (text[0] == ' ' || text[^1] == ' '));
        if (!quote)
        {
            output.Write(text);
            return;
        }

        output.Write('"');
        output.Write(text.Replace("\"", "\"\""));
        output.Write('"');
    }
}
