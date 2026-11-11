using System.Text;

namespace CopybookKit.Parsing;

/// <summary>What a PICTURE string says about a field's category and size.</summary>
internal readonly record struct PictureInfo(bool IsNumeric, int Characters, int Digits, int Scale, bool IsSigned)
{
    private const int MaxExpandedLength = 1 << 20;

    public static PictureInfo Parse(string picture, int line)
    {
        string symbols = Expand(picture.ToUpperInvariant(), picture, line);
        if (symbols.AsSpan().ContainsAny('N', 'G'))
        {
            throw new CopybookException($"Picture '{picture}' is national or double-byte data, which is not supported.", line);
        }

        if (!symbols.AsSpan().ContainsAnyExcept("S9VP"))
        {
            return ParseNumeric(symbols, picture, line);
        }

        // Alphanumeric and edited pictures are text. V and P mark positions without using a byte.
        int characters = 0;
        foreach (char c in symbols)
        {
            if (c is not ('V' or 'P'))
            {
                characters++;
            }
        }

        if (characters == 0)
        {
            throw new CopybookException($"Picture '{picture}' has no character positions.", line);
        }

        return new PictureInfo(false, characters, 0, 0, false);
    }

    private static PictureInfo ParseNumeric(string symbols, string picture, int line)
    {
        bool signed = symbols[0] == 'S';
        string body = signed ? symbols[1..] : symbols;
        if (body.Contains('S'))
        {
            throw new CopybookException($"Picture '{picture}' has S somewhere other than the start.", line);
        }

        int point = body.IndexOf('V');
        if (point >= 0 && body.IndexOf('V', point + 1) >= 0)
        {
            throw new CopybookException($"Picture '{picture}' has more than one V.", line);
        }

        int digits = body.Count(c => c == '9');
        if (digits == 0)
        {
            throw new CopybookException($"Picture '{picture}' has no digit positions.", line);
        }

        string positions = point < 0 ? body : body.Remove(point, 1);
        int scale;
        if (positions[0] == 'P')
        {
            // PP99 is .0099: every position, P included, is after the decimal point.
            if (positions.TrimStart('P').Contains('P') || (point > 0 && body[..point].Contains('9')))
            {
                throw new CopybookException($"Picture '{picture}' mixes P with digits in an unsupported way.", line);
            }

            scale = positions.Length;
        }
        else if (positions[^1] == 'P')
        {
            // 99PP is 9900: the decimal point is after the last P.
            if (positions.TrimEnd('P').Contains('P') || (point >= 0 && point != body.Length - 1))
            {
                throw new CopybookException($"Picture '{picture}' mixes P with digits in an unsupported way.", line);
            }

            scale = -(positions.Length - digits);
        }
        else if (positions.Contains('P'))
        {
            throw new CopybookException($"Picture '{picture}' has P between digits.", line);
        }
        else
        {
            scale = point < 0 ? 0 : body.Length - point - 1;
        }

        return new PictureInfo(true, digits, digits, scale, signed);
    }

    private static string Expand(string picture, string original, int line)
    {
        var expanded = new StringBuilder();
        for (int i = 0; i < picture.Length; i++)
        {
            char c = picture[i];
            if (c is '(' or ')')
            {
                throw new CopybookException($"Picture '{original}' has a misplaced parenthesis.", line);
            }

            if (i + 1 < picture.Length && picture[i + 1] == '(')
            {
                int close = picture.IndexOf(')', i + 2);
                if (close < 0 || !int.TryParse(picture.AsSpan(i + 2, close - i - 2), out int repeat) || repeat < 1)
                {
                    throw new CopybookException($"Picture '{original}' has a bad repeat count.", line);
                }

                if (expanded.Length + repeat > MaxExpandedLength)
                {
                    throw new CopybookException($"Picture '{original}' is too long.", line);
                }

                expanded.Append(c, repeat);
                i = close;
            }
            else
            {
                expanded.Append(c);
            }
        }

        if (expanded.Length == 0)
        {
            throw new CopybookException("The picture string is empty.", line);
        }

        return expanded.ToString();
    }
}
