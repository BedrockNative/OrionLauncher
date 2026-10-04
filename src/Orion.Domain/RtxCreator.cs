using System.Globalization;
using System.Text.Json;

namespace Orion.Domain;

public sealed record RtxCreatorVersion(string Id, string Label, bool IsDefault);
public sealed record RtxCreatorCategory(string Id, string Label, IReadOnlyList<RtxCreatorField> Fields);
public sealed record RtxCreatorField(string Name, string Label, string Type, JsonElement Definition)
{
    public JsonElement Default => Definition.GetProperty("default");
    public string Description => Definition.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
    public double Minimum => Number("min", 0);
    public double Maximum => Number("max", 1);
    public double Step => Number("step", .01);
    private double Number(string key, double fallback) => Definition.TryGetProperty(key, out var n) ? n.GetDouble() : fallback;
    public IReadOnlyList<string> Options => Definition.TryGetProperty("options", out var options)
        ? options.EnumerateArray().Select(o => o.ValueKind == JsonValueKind.String ? o.GetString()! : o.GetProperty("label").GetString()!).ToArray() : [];
}

/// <summary>Declarative upstream data only. No scripts, expressions or macros are executed locally.</summary>
public sealed record RtxCreatorForm(IReadOnlyList<RtxCreatorCategory> Categories)
{
    public IReadOnlyList<RtxCreatorField> Fields => Categories.SelectMany(c => c.Fields).ToArray();
    public static RtxCreatorForm Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
        List<RtxCreatorCategory> categories = []; HashSet<string> names = []; HashSet<string> macros = [];
        foreach (var category in document.RootElement.GetProperty("categories").EnumerateArray())
        {
            List<RtxCreatorField> fields = [];
            foreach (var entry in category.GetProperty("fields").EnumerateArray())
            {
                var type = Text(entry, "type"); var name = Text(entry, "name"); var macro = Text(entry, "macro");
                if (type is not ("toggle" or "slider" or "select" or "color") || !Identifier(name) || !Identifier(macro)
                    || !names.Add(name) || !macros.Add(macro) || names.Count > 600)
                    throw new InvalidDataException("Unsupported or duplicate BetterRTX creator field.");
                var field = new RtxCreatorField(name, Text(entry, "label"), type, entry.Clone());
                Validate(field, field.Default);
                if (!double.IsFinite(field.Step) || field.Step <= 0) throw new InvalidDataException("Invalid creator increment.");
                fields.Add(field);
            }
            categories.Add(new(Text(category, "id"), Text(category, "label"), fields));
            if (categories.Count > 64) throw new InvalidDataException("Too many creator categories.");
        }
        var form = new RtxCreatorForm(categories);
        if (names.Count == 0) throw new InvalidDataException("Empty creator form.");
        foreach (var field in form.Fields)
        {
            if (field.Definition.TryGetProperty("intensityField", out var intensity) && !names.Contains(intensity.GetString()!))
                throw new InvalidDataException("Missing creator color intensity.");
            form.IsVisible(field, form.Defaults()); // Reject dependency cycles. Removed parents keep dependent fields hidden.
        }
        return form;
    }
    public Dictionary<string, JsonElement> Defaults() => Fields.ToDictionary(f => f.Name, f => f.Default);
    public bool IsVisible(RtxCreatorField field, IReadOnlyDictionary<string, JsonElement> values)
    {
        var fields = Fields.ToDictionary(f => f.Name); HashSet<string> visited = []; var visible = true;
        while (true)
        {
            if (!visited.Add(field.Name)) throw new InvalidDataException("Cyclic creator dependency.");
            if (field.Definition.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) visible = false;
            if (!field.Definition.TryGetProperty("showWhen", out var dependency)) return visible;
            var parent = dependency.GetProperty("field").GetString()!;
            // Upstream beta schemas can retain fields whose parent was removed. As in the
            // web form, these are inactive: never invent a parent value or emit their macros.
            if (!fields.TryGetValue(parent, out field!)) return false;
            visible &= values.TryGetValue(parent, out var actual) && JsonElement.DeepEquals(actual, dependency.GetProperty("value"));
        }
    }
    public string SerializeSettings(IReadOnlyDictionary<string, JsonElement> values)
    {
        Dictionary<string, object> output = [];
        foreach (var field in Fields)
        {
            if (!values.TryGetValue(field.Name, out var value)) throw new InvalidDataException("Missing setting: " + field.Name);
            Validate(field, value);
        }
        foreach (var field in Fields)
        {
            if (!IsVisible(field, values) || field.Definition.TryGetProperty("consumedBy", out _)) continue;
            var value = values[field.Name]; var definition = field.Definition;
            object converted = field.Type switch
            {
                "toggle" => value.GetBoolean(),
                "slider" => value.GetDouble() * (definition.TryGetProperty("macroScale", out var scale) ? scale.GetDouble() : 1),
                "select" => definition.TryGetProperty("macroMap", out var map) ? map.GetProperty(value.GetString()!).Clone() : value.GetString()!,
                "color" => Color(field, value, values),
                _ => throw new InvalidDataException("Unsupported creator field.")
            };
            output.Add(Text(definition, "macro"), converted);
        }
        return JsonSerializer.Serialize(output);
    }
    private static string Color(RtxCreatorField field, JsonElement value, IReadOnlyDictionary<string, JsonElement> values)
    {
        var channels = value.EnumerateArray().Select(v => v.GetDouble()).ToList();
        if (field.Definition.TryGetProperty("intensityField", out var intensity)) channels.Add(values[intensity.GetString()!].GetDouble());
        return $"float{channels.Count}({string.Join(", ", channels.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))})";
    }
    private static void Validate(RtxCreatorField field, JsonElement value)
    {
        bool Number(JsonElement n) => n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out var v) && double.IsFinite(v)
            && double.IsFinite(field.Minimum) && double.IsFinite(field.Maximum) && field.Minimum <= field.Maximum && v >= field.Minimum && v <= field.Maximum;
        var valid = field.Type switch
        {
            "toggle" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "slider" => Number(value),
            "select" => value.ValueKind == JsonValueKind.String && field.Options.Contains(value.GetString()),
            "color" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 3 && value.EnumerateArray().All(Number),
            _ => false
        };
        if (!valid) throw new InvalidDataException("Invalid creator value: " + field.Name);
    }
    private static bool Identifier(string text) => text.Length is > 0 and <= 128 && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static string Text(JsonElement element, string key) => element.GetProperty(key).GetString() is { Length: > 0 and <= 300 } text
        ? text : throw new InvalidDataException("Invalid creator metadata.");
}
