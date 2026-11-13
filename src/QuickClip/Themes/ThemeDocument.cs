using System.Text.Json;
using System.Windows.Media;

namespace QuickClip.Themes;

internal sealed record ThemeDocument(string Name, IReadOnlyDictionary<ThemeColor, Color> Palette)
{
    internal const int MaxBytes = 8192;
    internal const int MaxNameLength = 48;
    internal const int Version = 1;

    internal static ThemeDocument Read(Stream stream)
    {
        // Read at most one byte beyond the limit, including on non-seekable streams.
        var bytes = new byte[MaxBytes + 1];
        int size = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (size > MaxBytes) throw new InvalidDataException("Theme files must be 8 KiB or smaller.");
        try
        {
            int offset = size >= 3 && bytes.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            using var json = JsonDocument.Parse(bytes.AsMemory(offset, size - offset), new JsonDocumentOptions { MaxDepth = 4 });
            var fields = Fields(json.RootElement, ["version", "name", "colors"]);
            if (fields["version"].ValueKind != JsonValueKind.Number || !fields["version"].TryGetInt32(out int version) || version != Version)
                throw new InvalidDataException("Unsupported theme version.");
            string name = ValidateName(fields["name"].ValueKind == JsonValueKind.String ? fields["name"].GetString() : null);
            var colors = Fields(fields["colors"], ThemePalette.Colors.Select(key => key.ToString()).ToArray());
            var palette = new Dictionary<ThemeColor, Color>();
            foreach (var key in ThemePalette.Colors)
            {
                var value = colors[key.ToString()];
                if (value.ValueKind != JsonValueKind.String || !ThemePalette.TryParseColor(value.GetString(), out var color))
                    throw new InvalidDataException($"Use #RRGGBB for {key}.");
                palette.Add(key, color);
            }
            return new ThemeDocument(name, palette);
        }
        catch (JsonException ex) { throw new InvalidDataException("The theme file is invalid JSON.", ex); }
    }

    internal string Validate() => ValidateName(Name);

    internal void Write(Stream stream)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("version", Version);
        writer.WriteString("name", Validate());
        writer.WriteStartObject("colors");
        foreach (var key in ThemePalette.Colors) writer.WriteString(key.ToString(), ThemePalette.Hex(Palette[key]));
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string ValidateName(string? value)
    {
        string name = value?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
            throw new InvalidDataException($"Enter a theme name from 1 to {MaxNameLength} characters.");
        return name;
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement element, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a JSON object.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !result.TryAdd(field.Name, field.Value))
                throw new InvalidDataException("The theme has an unknown or duplicate field.");
        if (result.Count != allowed.Length) throw new InvalidDataException("The theme is missing required fields.");
        return result;
    }
}
