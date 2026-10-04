using System.Text;

namespace Orion.Domain;

/// <summary>A small argv parser, never a shell. The placeholder expands to a complete argument vector.</summary>
public static class LaunchCommandTemplate
{
    public static string[] Parse(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return ["%command%"];
        if (template.Length > 8192 || template.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Launch command must be a single line of at most 8192 characters.");
        List<string> result = []; StringBuilder word = new();
        char quote = '\0'; bool started = false, escaped = false;
        foreach (var c in template)
        {
            if (escaped) { word.Append(c); escaped = false; started = true; continue; }
            if (c == '\\' && quote != '\'') { escaped = true; started = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; else word.Append(c); started = true; continue; }
            if (c is '\'' or '"') { quote = c; started = true; continue; }
            if (char.IsWhiteSpace(c)) { if (started) { result.Add(word.ToString()); word.Clear(); started = false; } continue; }
            if (c is ';' or '|' or '&' or '<' or '>' or '`')
                throw new ArgumentException("Shell operators are not supported. Use a wrapper such as prime-run %command%.");
            word.Append(c); started = true;
        }
        if (escaped || quote != '\0') throw new ArgumentException("Unclosed quote or escape in launch command.");
        if (started) result.Add(word.ToString());
        if (result.Count > 128 || result.Count(x => x == "%command%") != 1 || result[^1] != "%command%"
            || result.Any(x => x != "%command%" && x.Contains("%command%", StringComparison.Ordinal)) || result[0].Length == 0)
            throw new ArgumentException("Use exactly one %command%, as the final argument: prime-run %command%.");
        return result.ToArray();
    }
}
