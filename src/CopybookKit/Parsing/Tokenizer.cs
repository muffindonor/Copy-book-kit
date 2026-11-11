using System.Text;

namespace CopybookKit.Parsing;

internal readonly record struct Token(string Text, int Line, bool IsLiteral)
{
    public bool Is(string keyword) => !IsLiteral && Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Splits prepared copybook code into statements, each ended by a period.</summary>
internal static class Tokenizer
{
    public static List<List<Token>> Split(string code)
    {
        var statements = new List<List<Token>>();
        var tokens = new List<Token>();
        var current = new StringBuilder();
        int line = 1;
        int tokenLine = 1;
        int i = 0;

        while (i < code.Length)
        {
            char c = code[i];
            if (c == '\n')
            {
                line++;
                i++;
                if (i < code.Length && code[i] == SourceText.Continuation)
                {
                    // The word carries on from the first non-blank column of this line.
                    i++;
                    while (i < code.Length && code[i] == ' ')
                    {
                        i++;
                    }
                }
                else
                {
                    Flush();
                }
            }
            else if (c is ' ' or '\r' or SourceText.Continuation)
            {
                Flush();
                i++;
            }
            else if (c == '*' && i + 1 < code.Length && code[i + 1] == '>')
            {
                Flush();
                while (i < code.Length && code[i] != '\n')
                {
                    i++;
                }
            }
            else if (c is '"' or '\'')
            {
                // A prefix such as the X in X'00' belongs to the literal.
                int start = current.Length == 0 ? line : tokenLine;
                current.Clear();
                i = SkipLiteral(code, i, ref line);
                tokens.Add(new Token("literal", start, IsLiteral: true));
            }
            else if (c is ',' or ';' && EndsWord(code, i + 1))
            {
                Flush();
                i++;
            }
            else if (c == '.' && EndsWord(code, i + 1))
            {
                Flush();
                EndStatement();
                i++;
            }
            else
            {
                if (current.Length == 0)
                {
                    tokenLine = line;
                }

                current.Append(c);
                i++;
            }
        }

        // Copybook fragments often leave out the last period.
        Flush();
        EndStatement();
        return statements;

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(new Token(current.ToString(), tokenLine, IsLiteral: false));
                current.Clear();
            }
        }

        void EndStatement()
        {
            if (tokens.Count > 0)
            {
                statements.Add(tokens);
                tokens = [];
            }
        }
    }

    private static bool EndsWord(string code, int index) => index >= code.Length || char.IsWhiteSpace(code[index]);

    /// <summary>Returns the index after the literal that opens at <paramref name="i"/>.</summary>
    private static int SkipLiteral(string code, int i, ref int line)
    {
        char quote = code[i];
        int openedOn = line;
        i++;
        while (i < code.Length)
        {
            char c = code[i];
            if (c == quote)
            {
                if (i + 1 < code.Length && code[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (c == '\n')
            {
                if (i + 1 >= code.Length || code[i + 1] != SourceText.Continuation)
                {
                    break;
                }

                // A continued literal reopens with its own quote on the next line.
                line++;
                i += 2;
                while (i < code.Length && code[i] == ' ')
                {
                    i++;
                }

                if (i < code.Length && code[i] == quote)
                {
                    i++;
                }

                continue;
            }

            i++;
        }

        throw new CopybookException("A literal is missing its closing quote.", openedOn);
    }
}
