using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickClip.Media;

namespace QuickClip;

internal sealed record SharingProfile(Guid Id, string Name, double LimitMB)
{
    public override string ToString() => $"{Name} · {LimitMB:0.######} MB";
}

internal static class SharingProfiles
{
    internal const int MaxProfiles = 32;
    private static readonly Guid MigratedId = new("1d83a9c3-7091-4ea0-9a61-1bf613a06408");

    internal static bool TryLimit(string text, out double limit) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out limit) && TargetSizeExport.IsValidLimit(limit);

    internal static bool ValidName(string? name) =>
        name is { Length: > 0 and <= 64 } && !name.Any(char.IsControl) && !string.IsNullOrWhiteSpace(name);

    internal static SharingProfile[] Get(AppSettings settings)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        var profiles = (settings.ShareProfiles ?? []).Where(profile =>
            profile != null && profile.Id != Guid.Empty && ValidName(profile.Name) &&
            TargetSizeExport.IsValidLimit(profile.LimitMB) && !names.Contains(profile.Name.Trim()) && ids.Add(profile.Id) &&
            names.Add(profile.Name.Trim())).Take(MaxProfiles)
            .Select(profile => profile with { Name = profile.Name.Trim() }).ToArray();
        return profiles.Length != 0 ? profiles :
            [new(MigratedId, "Default", TargetSizeExport.IsValidLimit(settings.ShareSizeMB) ? settings.ShareSizeMB : ShareCopy.LimitMB)];
    }

    internal static SharingProfile Default(AppSettings settings)
    {
        var profiles = Get(settings);
        return Guid.TryParse(settings.DefaultShareProfile, out var id) ? profiles.FirstOrDefault(p => p.Id == id) ?? profiles[0] : profiles[0];
    }

    internal static bool TrySave(AppSettings settings, IReadOnlyList<SharingProfile> profiles, Guid defaultId)
    {
        var oldProfiles = settings.ShareProfiles;
        string? oldId = settings.DefaultShareProfile;
        double oldLimit = settings.ShareSizeMB;
        settings.ShareProfiles = profiles.ToArray();
        settings.DefaultShareProfile = defaultId.ToString();
        settings.ShareSizeMB = Default(settings).LimitMB;
        if (settings.TrySave()) return true;
        settings.ShareProfiles = oldProfiles;
        settings.DefaultShareProfile = oldId;
        settings.ShareSizeMB = oldLimit;
        return false;
    }

    // Read broken rows independently so one profile cannot reset unrelated settings.
    internal sealed class ListConverter : JsonConverter<SharingProfile[]>
    {
        public ListConverter() { }

        public override SharingProfile[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var value = JsonDocument.ParseValue(ref reader);
            if (value.RootElement.ValueKind != JsonValueKind.Array) return [];
            var profiles = new List<SharingProfile>();
            foreach (var entry in value.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty(nameof(SharingProfile.Id), out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var key) &&
                    entry.TryGetProperty(nameof(SharingProfile.Name), out var name) && name.ValueKind == JsonValueKind.String &&
                    entry.TryGetProperty(nameof(SharingProfile.LimitMB), out var mb) && mb.ValueKind == JsonValueKind.Number && mb.TryGetDouble(out var size))
                    profiles.Add(new(key, name.GetString()!, size));
            }
            return profiles.ToArray();
        }

        public override void Write(Utf8JsonWriter writer, SharingProfile[] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var profile in value) JsonSerializer.Serialize(writer, profile, options);
            writer.WriteEndArray();
        }
    }

    internal sealed class IdConverter : JsonConverter<string>
    {
        public IdConverter() { }

        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString();
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}
