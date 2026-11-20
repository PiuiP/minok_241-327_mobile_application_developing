using System.Globalization;

namespace PhotoGeoEditor;

/// <summary>Простой разбор аргументов командной строки: позиционные + --имя [значение].</summary>
internal sealed class ParsedArgs
{
    // Эти опции — флаги, значения у них не бывает.
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "recursive", "overwrite", "help"
    };

    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = new();

    public static ParsedArgs Parse(IEnumerable<string> args)
    {
        var result = new ParsedArgs();
        var list = args.ToList();

        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? value = null;
            if (!Flags.Contains(name) && i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                value = list[++i]; // значение вроде -33.8 тоже подходит: оно начинается с одного "-"

            result._options[name] = value;
        }

        return result;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name)
    {
        if (!_options.TryGetValue(name, out var value)) return null;
        if (value is null) throw new ArgumentException($"Option --{name} requires a value.");
        return value;
    }

    public string RequirePositional(int index, string description)
    {
        if (index >= Positional.Count) throw new ArgumentException($"Missing argument: {description}.");
        return Positional[index];
    }

    public double? Double(string name)
    {
        var text = Get(name);
        if (text is null) return null;
        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"Option --{name} expects a number, got '{text}'.");
        return value;
    }

    public int? Int(string name)
    {
        var text = Get(name);
        if (text is null) return null;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"Option --{name} expects an integer, got '{text}'.");
        return value;
    }

    /// <summary>Время в UTC, формат: "2026-11-18 09:30:00".</summary>
    public DateTime? Time(string name)
    {
        var text = Get(name);
        if (text is null) return null;
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw new FormatException($"Option --{name} expects a date/time like \"2026-11-18 09:30:00\", got '{text}'.");
        return value;
    }
}
