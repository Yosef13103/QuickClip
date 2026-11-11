using System.Collections.Immutable;
using System.Globalization;

namespace QuickClip.Discovery;

internal sealed record SteamAppManifest(uint AppId, string Name, string InstallDirectory);

/// <summary>Pure parsers for the Valve KeyValues subset used by Steam's local manifests.</summary>
internal static class SteamManifestParser
{
    internal const int MaxDocumentSize = 1024 * 1024;

    public static SteamAppManifest? ParseAppManifest(string text)
    {
        var app = Parse(text)?.GetValueOrDefault("AppState")?.Children;
        if (app == null || !TryId(app.GetValueOrDefault("appid")?.Text, out uint id))
            return null;
        string? name = app.GetValueOrDefault("name")?.Text;
        string? install = app.GetValueOrDefault("installdir")?.Text;
        return !string.IsNullOrWhiteSpace(name) && CatalogPaths.SafeLeaf(install)
            ? new(id, name.Trim(), install!) : null;
    }

    /// <summary>Suggestions only. The caller must explicitly approve roots before a scan.</summary>
    public static ImmutableArray<string> ParseLibraryFolders(string text)
    {
        var folders = Parse(text)?.GetValueOrDefault("libraryfolders")?.Children;
        if (folders == null) return [];
        return folders.Where(p => uint.TryParse(p.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(p => CatalogPaths.LocalPath(p.Value.Text ?? p.Value.Children?.GetValueOrDefault("path")?.Text))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    internal static bool TryId(string? text, out uint id) =>
        uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;

    private static Dictionary<string, Value>? Parse(string text)
    {
        if (text.Length > MaxDocumentSize) return null;
        try { return new Reader(text).ReadObject(0); }
        catch (FormatException) { return null; }
    }

    private sealed record Value(string? Text, Dictionary<string, Value>? Children = null);

    private sealed class Reader(string text)
    {
        private int _position = text.StartsWith('\uFEFF') ? 1 : 0;

        public Dictionary<string, Value> ReadObject(int depth)
        {
            if (depth > 16) throw new FormatException();
            var values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                SkipTrivia();
                if (_position == text.Length)
                    return depth == 0 ? values : throw new FormatException();
                if (text[_position] == '}')
                {
                    if (depth == 0) throw new FormatException();
                    _position++;
                    return values;
                }

                string key = ReadToken();
                SkipTrivia();
                Value value;
                if (_position < text.Length && text[_position] == '{')
                {
                    _position++;
                    value = new(null, ReadObject(depth + 1));
                }
                else value = new(ReadToken());
                if (!values.TryAdd(key, value)) throw new FormatException();
            }
        }

        private string ReadToken()
        {
            if (_position == text.Length || text[_position] is '{' or '}') throw new FormatException();
            if (text[_position] != '"')
            {
                int start = _position;
                while (_position < text.Length && !char.IsWhiteSpace(text[_position]) &&
                    text[_position] is not ('{' or '}' or '"')) _position++;
                return text[start.._position];
            }

            _position++;
            var value = new System.Text.StringBuilder();
            while (_position < text.Length)
            {
                char c = text[_position++];
                if (c == '"') return value.ToString();
                if (c == '\\' && _position < text.Length && text[_position] is '\\' or '"')
                    c = text[_position++];
                value.Append(c);
            }
            throw new FormatException();
        }

        private void SkipTrivia()
        {
            while (_position < text.Length)
            {
                if (char.IsWhiteSpace(text[_position])) _position++;
                else if (text.AsSpan(_position).StartsWith("//", StringComparison.Ordinal))
                {
                    while (_position < text.Length && text[_position] is not ('\r' or '\n')) _position++;
                }
                else break;
            }
        }
    }
}
