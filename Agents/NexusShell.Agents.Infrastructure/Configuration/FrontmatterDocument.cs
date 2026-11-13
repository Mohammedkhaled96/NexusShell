namespace NexusShell.Agents.Infrastructure.Configuration;

/// <summary>
/// Minimal, strict YAML front-matter reader for expert files. Supports the subset
/// used by expert definitions:
/// <code>
/// ---
/// name: Shell Expert
/// keywords: [powershell, cmd, "wsl"]
/// tools:
///   - shell
///   - filesystem
/// description: >
///   folded text that
///   spans lines
/// ---
/// Markdown body = instructions
/// </code>
/// </summary>
internal sealed class FrontmatterDocument
{
    private readonly Dictionary<string, object> _fields;

    private FrontmatterDocument(Dictionary<string, object> fields, string body)
    {
        _fields = fields;
        Body = body;
    }

    public string Body { get; }

    public bool HasFrontmatter => _fields.Count > 0;

    public static FrontmatterDocument Parse(string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿');
        var fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return new FrontmatterDocument(fields, normalized.Trim());

        int end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) throw new FormatException("Front-matter starts with '---' but has no closing '---' line.");

        string header = normalized[4..end];
        int bodyStart = normalized.IndexOf('\n', end + 1);
        string body = bodyStart < 0 ? string.Empty : normalized[(bodyStart + 1)..];

        var lines = header.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            if (char.IsWhiteSpace(line[0])) throw new FormatException($"Unexpected indentation in front-matter: '{line.Trim()}'.");

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) throw new FormatException($"Expected 'key: value' in front-matter, got '{line.Trim()}'.");

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();

            if (value is ">" or "|" or ">-" or "|-")
            {
                var block = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                    block.Add(lines[++i].Trim());
                fields[key] = value.StartsWith('>')
                    ? string.Join(' ', block.Where(b => b.Length > 0))
                    : string.Join('\n', block).Trim('\n');
            }
            else if (value.Length == 0)
            {
                var items = new List<string>();
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith("- ", StringComparison.Ordinal) && char.IsWhiteSpace(lines[i + 1][0]))
                    items.Add(Unquote(lines[++i].TrimStart()[2..].Trim()));
                fields[key] = items;
            }
            else if (value.StartsWith('[') && value.EndsWith(']'))
            {
                fields[key] = SplitInlineList(value[1..^1]);
            }
            else
            {
                fields[key] = Unquote(value);
            }
        }

        return new FrontmatterDocument(fields, body.Trim());
    }

    public string? GetString(params string[] keys)
    {
        foreach (string key in keys)
        {
            if (_fields.TryGetValue(key, out var value))
                return value is string s ? s : string.Join(", ", (List<string>)value);
        }
        return null;
    }

    public IReadOnlyList<string> GetList(params string[] keys)
    {
        foreach (string key in keys)
        {
            if (!_fields.TryGetValue(key, out var value)) continue;
            return value switch
            {
                List<string> list => list.Where(v => v.Length > 0).ToList(),
                string s when s.Length > 0 => SplitInlineList(s),
                _ => [],
            };
        }
        return [];
    }

    public bool? GetBool(params string[] keys)
    {
        string? value = GetString(keys);
        return value is null ? null : bool.TryParse(value, out bool b) ? b : throw new FormatException($"'{value}' is not true/false.");
    }

    public double? GetDouble(params string[] keys)
    {
        string? value = GetString(keys);
        return value is null
            ? null
            : double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)
                ? d
                : throw new FormatException($"'{value}' is not a number.");
    }

    private static List<string> SplitInlineList(string text) =>
        text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Unquote)
            .Where(v => v.Length > 0)
            .ToList();

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }
}
