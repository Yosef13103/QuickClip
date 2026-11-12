using System.Text.Json;

namespace QuickClip.Discovery;

internal sealed record EpicInstallManifest(string Id, string Name, string InstallDirectory, string Executable);

/// <summary>Conservative subset of Epic Launcher's local .item format.</summary>
internal static class EpicManifestParser
{
    internal const int MaxDocumentSize = 256 * 1024;

    public static EpicInstallManifest? Parse(ReadOnlyMemory<byte> data, out bool malformed)
    {
        malformed = data.IsEmpty || data.Length > MaxDocumentSize;
        if (malformed) return null;
        if (data.Span.StartsWith("\uFEFF"u8)) data = data[3..];
        try
        {
            using var document = JsonDocument.Parse(data, new() { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                malformed = true;
                return null;
            }
            if (!Flag(root, "bIsExecutable", true) ||
                Flag(root, "bIsApplication", false) || Flag(root, "bIsIncompleteInstall", true)) return null;
            var app = Text(root, "AppName");
            var main = Text(root, "MainGameAppName");
            var name = Text(root, "DisplayName")?.Trim();
            var install = CatalogPaths.LocalPath(Text(root, "InstallLocation"));
            var relative = Text(root, "LaunchExecutable");
            if (app is not { Length: >= 1 and <= 128 } ||
                !app.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.') ||
                app.StartsWith("UE_", StringComparison.OrdinalIgnoreCase) ||
                (main is { Length: > 0 } && !main.Equals(app, StringComparison.OrdinalIgnoreCase)) ||
                name is not { Length: >= 1 and <= 128 } || name.Any(char.IsControl) ||
                install is not { Length: > 3 } || relative is not { Length: > 0 and <= 512 } ||
                relative.Split('\\', '/').Any(part => !CatalogPaths.SafeLeaf(part)))
                return null;
            var executable = CatalogPaths.LocalPath(Path.Combine(install, relative));
            return executable != null && CatalogPaths.IsWithin(executable, install) && CatalogPaths.IsExecutable(executable)
                ? new("epic:" + app.ToLowerInvariant(), name, install, executable) : null;
        }
        catch (JsonException) { malformed = true; return null; }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool Flag(JsonElement element, string name, bool expected) =>
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);
}
