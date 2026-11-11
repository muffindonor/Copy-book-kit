using System.Globalization;

namespace CopybookKit.Parsing;

/// <summary>Turns copybook source into a tree of <see cref="DataItem"/>, with sizes and offsets worked out.</summary>
internal static class CopybookParser
{
    /// <summary><c>decimal</c> holds 28 digits exactly. COBOL allows 18, or 31 with an IBM compiler option.</summary>
    private const int MaxDigits = 28;

    private const int MaxBinaryDigits = 18;
    private const string ImplicitRecordName = "RECORD";

    private static readonly HashSet<string> ClauseKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "PIC", "PICTURE", "USAGE", "SIGN", "LEADING", "TRAILING", "OCCURS", "DEPENDING", "ASCENDING", "DESCENDING",
        "INDEXED", "REDEFINES", "VALUE", "VALUES", "JUSTIFIED", "JUST", "BLANK", "SYNCHRONIZED", "SYNC", "EXTERNAL",
        "GLOBAL", "DISPLAY", "DISPLAY-1", "NATIONAL", "BINARY", "PACKED-DECIMAL", "INDEX", "POINTER",
        "PROCEDURE-POINTER", "FUNCTION-POINTER", "COMP", "COMP-1", "COMP-2", "COMP-3", "COMP-4", "COMP-5", "COMP-6",
        "COMP-X", "COMPUTATIONAL", "COMPUTATIONAL-1", "COMPUTATIONAL-2", "COMPUTATIONAL-3", "COMPUTATIONAL-4",
        "COMPUTATIONAL-5",
    };

    private enum Usage
    {
        Unspecified,
        Display,
        Binary,
        NativeBinary,
        Packed,
        Float,
        Double,
        Index,
    }

    private enum SignClause
    {
        Unspecified,
        Trailing,
        Leading,
        TrailingSeparate,
        LeadingSeparate,
    }

    /// <summary>An entry as written, before clauses inherited from its groups are applied.</summary>
    private sealed class Entry(DataItem item)
    {
        public DataItem Item { get; } = item;

        public List<Entry> Children { get; } = [];

        public string? Picture { get; set; }

        public Usage Usage { get; set; }

        public SignClause Sign { get; set; }

        public string? RedefinesName { get; set; }

        public string? DependingOnName { get; set; }

        public bool IsImplicit { get; init; }
    }

    public static Copybook Parse(string source, SourceFormat format)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<Entry> records = BuildTree(Tokenizer.Split(SourceText.Prepare(source, format)));
        if (records.Count == 0)
        {
            throw new CopybookException("No data items found.", 0);
        }

        var all = new List<DataItem>();
        foreach (Entry record in records)
        {
            Finish(record, Usage.Unspecified, SignClause.Unspecified);
            Number(record.Item, all);
        }

        ResolveRedefines(records, parent: null);
        if (records.Count > 1)
        {
            DataItem[] alternatives = [.. records.Select(r => r.Item)];
            foreach (DataItem record in alternatives)
            {
                record.Alternatives = alternatives;
            }
        }

        int nextSlot = 0;
        foreach (Entry record in records)
        {
            ResolveDependingOn(record, record.Item);
            bool variable = false;
            Layout(record.Item, 0, ref variable);
            AssignSlots(record.Item, ref nextSlot);
            SetOccursChains(record.Item, []);
        }

        return new Copybook([.. records.Select(r => r.Item)], all, nextSlot);
    }

    private static List<Entry> BuildTree(List<List<Token>> statements)
    {
        var records = new List<Entry>();
        var open = new Stack<Entry>();

        foreach (List<Token> statement in statements)
        {
            int index = 0;
            while (index < statement.Count && IsListingDirective(statement[index]))
            {
                index++;
            }

            if (index == statement.Count)
            {
                continue;
            }

            Token first = statement[index];
            if (first.Is("COPY") || first.Is("REPLACE") || first.Is("EXEC") || first.Text.StartsWith("++", StringComparison.Ordinal))
            {
                throw new CopybookException($"{first.Text.ToUpperInvariant()} statements are not supported. Expand nested copybooks first.", first.Line);
            }

            if (first.IsLiteral || !int.TryParse(first.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int level))
            {
                throw new CopybookException($"Expected a level number but found '{first.Text}'.", first.Line);
            }

            // Condition names and RENAMES describe existing storage, so they don't change the layout.
            if (level is 88 or 66)
            {
                continue;
            }

            if (level == 77)
            {
                level = 1;
            }

            if (level is < 1 or > 49)
            {
                throw new CopybookException($"Level {level} is not a valid level number.", first.Line);
            }

            index++;
            bool named = index < statement.Count && !statement[index].IsLiteral && !ClauseKeywords.Contains(statement[index].Text);
            bool filler = !named || statement[index].Is("FILLER");
            string name = filler ? "FILLER" : statement[index].Text;
            if (named)
            {
                index++;
            }

            var entry = new Entry(new DataItem(name, level, first.Line, filler));
            ParseClauses(statement, index, entry);

            while (open.Count > 0 && open.Peek().Item.Level >= level)
            {
                open.Pop();
            }

            if (open.Count == 0)
            {
                if (level == 1)
                {
                    records.Add(entry);
                }
                else
                {
                    // A copybook meant to be copied under a program's own 01 starts at a lower level.
                    if (records.Count == 0 || !records[^1].IsImplicit)
                    {
                        records.Add(new Entry(new DataItem(ImplicitRecordName, 1, first.Line, isFiller: false)) { IsImplicit = true });
                    }

                    records[^1].Children.Add(entry);
                    open.Push(records[^1]);
                }
            }
            else
            {
                Entry parent = open.Peek();
                if (parent.Picture is not null)
                {
                    throw new CopybookException($"{parent.Item.Name} has a PICTURE, so it can't have subordinate items.", first.Line);
                }

                parent.Children.Add(entry);
            }

            open.Push(entry);
        }

        return records;
    }

    private static bool IsListingDirective(Token token) =>
        token.Is("EJECT") || token.Is("SKIP1") || token.Is("SKIP2") || token.Is("SKIP3");

    private static void ParseClauses(List<Token> tokens, int index, Entry entry)
    {
        DataItem item = entry.Item;
        while (index < tokens.Count)
        {
            Token token = tokens[index++];
            string word = token.IsLiteral ? string.Empty : token.Text.ToUpperInvariant();
            switch (word)
            {
                case "PIC" or "PICTURE":
                    SkipWord(tokens, ref index, "IS");
                    entry.Picture = Next(tokens, ref index, token, "a picture string").Text;
                    break;

                case "USAGE":
                    SkipWord(tokens, ref index, "IS");
                    Token usage = Next(tokens, ref index, token, "a usage");
                    entry.Usage = ParseUsage(usage.Text.ToUpperInvariant(), usage);
                    break;

                case "SIGN":
                    SkipWord(tokens, ref index, "IS");
                    Token position = Next(tokens, ref index, token, "LEADING or TRAILING");
                    entry.Sign = ParseSign(position, tokens, ref index);
                    break;

                case "LEADING" or "TRAILING":
                    entry.Sign = ParseSign(token, tokens, ref index);
                    break;

                case "OCCURS":
                    ParseOccurs(tokens, ref index, token, item);
                    break;

                case "DEPENDING":
                    SkipWord(tokens, ref index, "ON");
                    entry.DependingOnName = Next(tokens, ref index, token, "a data name").Text;
                    break;

                case "REDEFINES":
                    entry.RedefinesName = Next(tokens, ref index, token, "a data name").Text;
                    break;

                case "VALUE" or "VALUES" or "ASCENDING" or "DESCENDING" or "INDEXED":
                    // Initial values, keys and index names don't change how a record is stored.
                    while (index < tokens.Count && (tokens[index].IsLiteral || !ClauseKeywords.Contains(tokens[index].Text)))
                    {
                        index++;
                    }

                    break;

                case "JUSTIFIED" or "JUST":
                    SkipWord(tokens, ref index, "RIGHT");
                    break;

                case "BLANK":
                    SkipWord(tokens, ref index, "WHEN");
                    if (!SkipWord(tokens, ref index, "ZERO") && !SkipWord(tokens, ref index, "ZEROS"))
                    {
                        SkipWord(tokens, ref index, "ZEROES");
                    }

                    break;

                case "EXTERNAL" or "GLOBAL":
                    break;

                case "SYNCHRONIZED" or "SYNC":
                    throw new CopybookException(
                        $"{item.Name} is SYNCHRONIZED. Slack bytes are not supported, so its offsets can't be trusted.", token.Line);

                default:
                    if (word.Length > 0 && ClauseKeywords.Contains(word))
                    {
                        entry.Usage = ParseUsage(word, token);
                        break;
                    }

                    throw new CopybookException($"Unexpected '{token.Text}' in the description of {item.Name}.", token.Line);
            }
        }

        if (entry.DependingOnName is not null && !item.HasOccurs)
        {
            throw new CopybookException($"{item.Name} has DEPENDING ON without OCCURS.", item.Line);
        }
    }

    private static void ParseOccurs(List<Token> tokens, ref int index, Token clause, DataItem item)
    {
        Token first = Next(tokens, ref index, clause, "a number of occurrences");
        if (first.Is("UNBOUNDED"))
        {
            throw new CopybookException($"{item.Name} has OCCURS UNBOUNDED, which is not supported.", first.Line);
        }

        int min = ParseCount(first);
        int max = min;
        if (SkipWord(tokens, ref index, "TO"))
        {
            Token upper = Next(tokens, ref index, clause, "a maximum number of occurrences");
            if (upper.Is("UNBOUNDED"))
            {
                throw new CopybookException($"{item.Name} has OCCURS UNBOUNDED, which is not supported.", upper.Line);
            }

            max = ParseCount(upper);
        }

        SkipWord(tokens, ref index, "TIMES");
        if (max < 1 || min > max)
        {
            throw new CopybookException($"{item.Name} has an OCCURS range of {min} to {max}, which is not valid.", clause.Line);
        }

        item.HasOccurs = true;
        item.MinOccurs = min;
        item.MaxOccurs = max;
    }

    private static int ParseCount(Token token)
    {
        if (token.IsLiteral || !int.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            throw new CopybookException($"Expected a number of occurrences but found '{token.Text}'.", token.Line);
        }

        return count;
    }

    private static SignClause ParseSign(Token position, List<Token> tokens, ref int index)
    {
        bool leading = position.Is("LEADING");
        if (!leading && !position.Is("TRAILING"))
        {
            throw new CopybookException($"Expected LEADING or TRAILING but found '{position.Text}'.", position.Line);
        }

        bool separate = SkipWord(tokens, ref index, "SEPARATE");
        if (separate)
        {
            SkipWord(tokens, ref index, "CHARACTER");
        }

        return (leading, separate) switch
        {
            (true, true) => SignClause.LeadingSeparate,
            (true, false) => SignClause.Leading,
            (false, true) => SignClause.TrailingSeparate,
            (false, false) => SignClause.Trailing,
        };
    }

    private static Usage ParseUsage(string word, Token token) => word switch
    {
        "DISPLAY" => Usage.Display,
        "COMP" or "COMPUTATIONAL" or "COMP-4" or "COMPUTATIONAL-4" or "BINARY" => Usage.Binary,
        "COMP-5" or "COMPUTATIONAL-5" => Usage.NativeBinary,
        "COMP-3" or "COMPUTATIONAL-3" or "PACKED-DECIMAL" => Usage.Packed,
        "COMP-1" or "COMPUTATIONAL-1" => Usage.Float,
        "COMP-2" or "COMPUTATIONAL-2" => Usage.Double,
        "INDEX" => Usage.Index,
        _ => throw new CopybookException($"USAGE {token.Text.ToUpperInvariant()} is not supported.", token.Line),
    };

    private static bool SkipWord(List<Token> tokens, ref int index, string word)
    {
        if (index < tokens.Count && tokens[index].Is(word))
        {
            index++;
            return true;
        }

        return false;
    }

    private static Token Next(List<Token> tokens, ref int index, Token clause, string expected)
    {
        if (index >= tokens.Count || tokens[index].IsLiteral)
        {
            throw new CopybookException($"Expected {expected} after {clause.Text.ToUpperInvariant()}.", clause.Line);
        }

        return tokens[index++];
    }

    /// <summary>Works out each item's kind and size, passing group-level USAGE and SIGN down to its fields.</summary>
    private static void Finish(Entry entry, Usage inheritedUsage, SignClause inheritedSign)
    {
        DataItem item = entry.Item;
        Usage usage = entry.Usage != Usage.Unspecified ? entry.Usage : inheritedUsage;
        SignClause sign = entry.Sign != SignClause.Unspecified ? entry.Sign : inheritedSign;

        if (item.Level == 1 && item.HasOccurs)
        {
            throw new CopybookException($"{item.Name} is a record, so it can't have OCCURS.", item.Line);
        }

        if (entry.Children.Count > 0)
        {
            item.Kind = ItemKind.Group;
            var children = new DataItem[entry.Children.Count];
            for (int i = 0; i < children.Length; i++)
            {
                Entry child = entry.Children[i];
                child.Item.Parent = item;
                Finish(child, usage, sign);
                children[i] = child.Item;
            }

            item.Children = children;
            return;
        }

        if (entry.Picture is null)
        {
            FinishWithoutPicture(entry, usage);
            return;
        }

        item.Picture = entry.Picture;
        PictureInfo picture = PictureInfo.Parse(entry.Picture, item.Line);
        if (!picture.IsNumeric)
        {
            // A group's USAGE only reaches its numeric fields, but one written on the field itself is a mistake.
            if (entry.Usage is not (Usage.Unspecified or Usage.Display) || entry.Sign != SignClause.Unspecified)
            {
                throw new CopybookException($"{item.Name} is not numeric, so it can't have a USAGE or SIGN clause.", item.Line);
            }

            item.Kind = ItemKind.Text;
            item.ValueType = ValueType.Text;
            item.Size = picture.Characters;
            return;
        }

        // Trailing P symbols add digits to the value, and together they must still fit a decimal.
        if (picture.Digits > MaxDigits || picture.Scale > MaxDigits || picture.Digits - picture.Scale > MaxDigits)
        {
            throw new CopybookException($"{item.Name} has more than {MaxDigits} digits, which is not supported.", item.Line);
        }

        item.Digits = picture.Digits;
        item.Scale = picture.Scale;
        item.IsSigned = picture.IsSigned;

        switch (usage)
        {
            case Usage.Unspecified or Usage.Display:
                item.Kind = ItemKind.ZonedDecimal;
                item.Sign = !picture.IsSigned ? SignPosition.None : sign switch
                {
                    SignClause.Leading => SignPosition.LeadingEmbedded,
                    SignClause.LeadingSeparate => SignPosition.LeadingSeparate,
                    SignClause.TrailingSeparate => SignPosition.TrailingSeparate,
                    _ => SignPosition.TrailingEmbedded,
                };
                item.Size = picture.Digits + (item.Sign is SignPosition.LeadingSeparate or SignPosition.TrailingSeparate ? 1 : 0);
                break;

            case Usage.Packed:
                item.Kind = ItemKind.PackedDecimal;
                item.Size = (picture.Digits / 2) + 1;
                break;

            case Usage.Binary or Usage.NativeBinary:
                if (picture.Digits > MaxBinaryDigits)
                {
                    throw new CopybookException($"{item.Name} is binary with more than {MaxBinaryDigits} digits.", item.Line);
                }

                item.Kind = ItemKind.Binary;
                item.IsNativeBinary = usage == Usage.NativeBinary;
                item.Size = picture.Digits switch
                {
                    <= 4 => 2,
                    <= 9 => 4,
                    _ => 8,
                };
                break;

            default:
                throw new CopybookException($"{item.Name} has a PICTURE, which its USAGE doesn't allow.", item.Line);
        }

        item.ValueType = IsInt64(item) ? ValueType.Int64 : ValueType.Decimal;
    }

    private static void FinishWithoutPicture(Entry entry, Usage usage)
    {
        DataItem item = entry.Item;
        switch (usage)
        {
            case Usage.Float:
                item.Kind = ItemKind.Float;
                item.Size = 4;
                item.ValueType = ValueType.Double;
                break;

            case Usage.Double:
                item.Kind = ItemKind.Double;
                item.Size = 8;
                item.ValueType = ValueType.Double;
                break;

            case Usage.Index:
                item.Kind = ItemKind.Binary;
                item.Size = 4;
                item.Digits = 9;
                item.IsSigned = true;
                item.IsNativeBinary = true;
                item.ValueType = ValueType.Int64;
                break;

            default:
                throw new CopybookException($"{item.Name} has neither a PICTURE nor subordinate items.", item.Line);
        }
    }

    private static bool IsInt64(DataItem item)
    {
        if (item.Scale > 0 || item.Digits - item.Scale > 18)
        {
            return false;
        }

        // An unsigned COMP-5 doubleword can hold values above long.MaxValue.
        return !(item.Kind == ItemKind.Binary && item.IsNativeBinary && !item.IsSigned && item.Size == 8);
    }

    private static void Number(DataItem item, List<DataItem> all)
    {
        item.Ordinal = all.Count;
        all.Add(item);
        foreach (DataItem child in item.Children)
        {
            Number(child, all);
        }
    }

    private static void ResolveRedefines(List<Entry> siblings, DataItem? parent)
    {
        for (int i = 0; i < siblings.Count; i++)
        {
            Entry entry = siblings[i];
            if (entry.RedefinesName is { } name)
            {
                DataItem? target = null;
                for (int j = i - 1; j >= 0 && target is null; j--)
                {
                    if (siblings[j].Item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && siblings[j].Item.Level == entry.Item.Level)
                    {
                        target = siblings[j].Item;
                    }
                }

                if (target is null)
                {
                    string where = parent is null ? "the copybook" : parent.Name;
                    throw new CopybookException(
                        $"{entry.Item.Name} redefines {name}, but no earlier item of that name and level exists in {where}.", entry.Item.Line);
                }

                // Redefining a redefinition shares the storage of the original item.
                target = target.Redefines ?? target;
                if (target.HasOccurs)
                {
                    throw new CopybookException($"{entry.Item.Name} redefines {target.Name}, which has OCCURS.", entry.Item.Line);
                }

                entry.Item.Redefines = target;
                if (parent is not null)
                {
                    List<DataItem> alternatives = target.Alternatives is null ? [target] : [.. target.Alternatives];
                    alternatives.Add(entry.Item);
                    foreach (DataItem alternative in alternatives)
                    {
                        alternative.Alternatives = alternatives;
                    }
                }
            }

            ResolveRedefines(entry.Children, entry.Item);
        }
    }

    private static void ResolveDependingOn(Entry entry, DataItem record)
    {
        if (entry.DependingOnName is { } name)
        {
            DataItem table = entry.Item;
            DataItem? counter = null;
            foreach (DataItem candidate in Descendants(record))
            {
                if (candidate.Ordinal >= table.Ordinal)
                {
                    break;
                }

                if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    counter = candidate;
                }
            }

            if (counter is null)
            {
                throw new CopybookException($"{table.Name} depends on {name}, which is not defined before it in the record.", table.Line);
            }

            if (counter.ValueType != ValueType.Int64)
            {
                throw new CopybookException($"{table.Name} depends on {name}, which is not an integer field.", table.Line);
            }

            for (DataItem? item = counter; item is not null; item = item.Parent)
            {
                if (item.HasOccurs)
                {
                    throw new CopybookException($"{table.Name} depends on {name}, which is inside a table.", table.Line);
                }
            }

            table.DependingOn = counter;
            counter.IsOdoObject = true;
        }

        foreach (Entry child in entry.Children)
        {
            ResolveDependingOn(child, record);
        }
    }

    private static IEnumerable<DataItem> Descendants(DataItem item)
    {
        yield return item;
        foreach (DataItem child in item.Children)
        {
            foreach (DataItem descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Layout(DataItem item, int start, ref bool variable)
    {
        item.Offset = start;
        item.IsVariablyLocated = variable;
        if (item.IsGroup)
        {
            long position = start;
            foreach (DataItem child in item.Children)
            {
                int childStart = child.Redefines?.Offset ?? (int)position;
                Layout(child, childStart, ref variable);
                position = Math.Max(position, childStart + ((long)child.Size * child.MaxOccurs));
                if (position > int.MaxValue)
                {
                    throw new CopybookException($"{item.Name} is larger than 2 GB.", item.Line);
                }
            }

            item.Size = (int)position - start;
        }

        if (item.DependingOn is not null)
        {
            variable = true;
        }
    }

    private static void AssignSlots(DataItem item, ref int next)
    {
        item.FirstSlot = next;
        if (item.IsGroup)
        {
            foreach (DataItem child in item.Children)
            {
                AssignSlots(child, ref next);
            }
        }
        else if (!item.IsFiller)
        {
            next++;
        }

        item.SlotsPerOccurrence = next - item.FirstSlot;
        long end = item.FirstSlot + ((long)item.SlotsPerOccurrence * item.MaxOccurs);
        if (end > Copybook.MaxSlots)
        {
            throw new CopybookException($"The copybook expands to more than {Copybook.MaxSlots:N0} fields.", item.Line);
        }

        next = (int)end;
    }

    private static void SetOccursChains(DataItem item, DataItem[] enclosing)
    {
        DataItem[] chain = item.HasOccurs ? [.. enclosing, item] : enclosing;
        item.OccursChain = chain;
        foreach (DataItem child in item.Children)
        {
            SetOccursChains(child, chain);
        }
    }
}
