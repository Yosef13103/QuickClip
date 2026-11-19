namespace QuickClip.Discovery;

/// <summary>Path-layout recognition only. A matching path is not a trusted
/// publisher, live process, eligible window, activity signal or capture grant.
/// Resolve those separately before any future runtime integration.</summary>
internal sealed class VersionedCompanionLayout
{
    internal string Root { get; }
    internal string ExecutableName { get; }

    private VersionedCompanionLayout(string root, string executableName)
    {
        Root = root;
        ExecutableName = executableName;
    }

    internal static VersionedCompanionLayout? Create(string root, string executableName)
    {
        if (HasTraversal(root) || CatalogPaths.LocalPath(root) is not { } local ||
            local == Path.GetPathRoot(local) || !CatalogPaths.SafeLeaf(executableName) ||
            !CatalogPaths.IsExecutable(executableName)) return null;
        var stem = Path.GetFileNameWithoutExtension(executableName).ToUpperInvariant();
        // Never use the shared Squirrel launcher/updater name as app identity.
        if (stem is "UPDATE" or "UPDATER" or "SETUP" or "INSTALL" or "INSTALLER" or "LAUNCHER" ||
            stem.EndsWith("HELPER", StringComparison.Ordinal) ||
            stem.EndsWith("LAUNCHER", StringComparison.Ordinal) ||
            stem.EndsWith("HELPER64", StringComparison.Ordinal) ||
            CatalogPaths.IsProtectedExecutable(Path.Combine(local, executableName))) return null;
        return new(local, executableName);
    }

    internal sealed record Candidate(string Path, Version Version);

    // Accept only the immediate version directory beneath the exact registered
    // root. Interpret the currently observed process path, never the newest
    // folder on disk. No recursive search or wildcard executable matching.
    internal Candidate? Match(string observedExecutable)
    {
        if (HasTraversal(observedExecutable) ||
            CatalogPaths.LocalPath(observedExecutable) is not { } path ||
            !string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(path) is not { } versionDirectory ||
            !string.Equals(Path.GetDirectoryName(versionDirectory), Root, StringComparison.OrdinalIgnoreCase))
            return null;
        string leaf = Path.GetFileName(versionDirectory);
        if (!leaf.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) return null;
        string version = leaf[4..];
        if (version.Length is 0 or > 64 ||
            version.Any(c => !char.IsAsciiDigit(c) && c != '.') ||
            !Version.TryParse(version, out var parsed)) return null;
        return new(path, parsed);
    }

    private static bool HasTraversal(string path) =>
        path.Split('\\', '/').Any(part => part is "." or "..");
}
