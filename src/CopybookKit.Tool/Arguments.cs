namespace CopybookKit.Tool;

/// <summary>The command line was wrong. The message is shown with a pointer to --help.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>
/// Parses <c>--name value</c> options, <c>--flag</c> switches and positional arguments.
/// Every option must be asked for, so that a mistyped one is reported and not ignored.
/// </summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = [];
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public Arguments(IEnumerable<string> args, IReadOnlySet<string> flagNames)
    {
        string? pending = null;
        foreach (string arg in args)
        {
            if (pending is not null)
            {
                Add(pending, arg);
                pending = null;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                string name = arg[2..];
                int equals = name.IndexOf('=');
                if (equals > 0)
                {
                    Add(name[..equals], name[(equals + 1)..]);
                }
                else if (flagNames.Contains(name))
                {
                    _flags.Add(name);
                }
                else
                {
                    pending = name;
                }
            }
            else
            {
                _positional.Add(arg);
            }
        }

        if (pending is not null)
        {
            throw new UsageException($"--{pending} needs a value.");
        }
    }

    public IReadOnlyList<string> Positional => _positional;

    public bool Flag(string name)
    {
        _used.Add(name);
        return _flags.Contains(name);
    }

    public string? Text(string name)
    {
        _used.Add(name);
        if (!_options.TryGetValue(name, out List<string>? values))
        {
            return null;
        }

        return values.Count == 1 ? values[0] : throw new UsageException($"--{name} was given more than once.");
    }

    public string Required(string name) => Text(name) ?? throw new UsageException($"--{name} is required.");

    public IReadOnlyList<string> All(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out List<string>? values) ? values : [];
    }

    public long? Number(string name)
    {
        string? text = Text(name);
        if (text is null)
        {
            return null;
        }

        return long.TryParse(text, out long value) && value >= 0
            ? value
            : throw new UsageException($"--{name} must be a whole number, but was '{text}'.");
    }

    public T Choice<T>(string name, T fallback, params (string Word, T Value)[] choices)
    {
        string? text = Text(name);
        if (text is null)
        {
            return fallback;
        }

        foreach ((string word, T value) in choices)
        {
            if (word.Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        throw new UsageException($"--{name} must be one of: {string.Join(", ", choices.Select(c => c.Word))}. It was '{text}'.");
    }

    /// <summary>Call after reading every option the command knows.</summary>
    public void RejectUnknown()
    {
        foreach (string name in _options.Keys.Concat(_flags))
        {
            if (!_used.Contains(name))
            {
                throw new UsageException($"Unknown option --{name}.");
            }
        }
    }

    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out List<string>? values))
        {
            _options[name] = values = [];
        }

        values.Add(value);
    }
}
