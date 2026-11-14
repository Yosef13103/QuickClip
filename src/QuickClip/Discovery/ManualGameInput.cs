using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace QuickClip.Discovery;

internal static class ManualGameInput
{
    private static readonly HashSet<string> GenericProductNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unreal Engine", "UnrealGame", "UE4Game", "BootstrapPackagedGame", "Unity",
        "Microsoft® Windows® Operating System", "Microsoft Windows Operating System",
    };

    internal sealed record Selection(string Executable, string? Shortcut);

    public static Selection Read(string input)
    {
        string path = CheckLocal(input, directory: false);
        if (CatalogPaths.IsExecutable(path)) return new(path, null);
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a Windows executable or shortcut.");

        string target;
        var link = (IShellLink)Activator.CreateInstance(Type.GetTypeFromCLSID(
            new("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var buffer = new StringBuilder(32768);
            // Read the stored target only. Resolve can search other locations or invoke shell handlers.
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
            target = Environment.ExpandEnvironmentVariables(buffer.ToString());
        }
        catch (COMException)
        {
            throw new IOException("Windows could not read this shortcut. Choose the executable directly.");
        }
        finally { Marshal.FinalReleaseComObject(link); }

        if (!CatalogPaths.IsExecutable(target))
            throw new IOException("The shortcut must point directly to a local Windows executable.");
        target = CheckLocal(target, directory: false);
        if (Path.GetFileNameWithoutExtension(target).ToUpperInvariant() is
            "CMD" or "POWERSHELL" or "PWSH" or "WSCRIPT" or "CSCRIPT" or "RUNDLL32" or "EXPLORER")
            throw new IOException("This shortcut opens a command or script host. Choose the game executable directly.");
        return new(target, path);
    }

    public static (IReadOnlyList<string> Paths, bool Limited) FindCandidates(string input)
    {
        string root = CheckLocal(input, directory: true);
        if (root.Length <= 3) throw new IOException("Choose the game's installation folder inside the drive.");
        var candidates = new List<string>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var clock = Stopwatch.StartNew();
        int visited = 0;
        bool limited = false;
        while (pending.TryDequeue(out var folder))
        {
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(folder.Path))
                {
                    if (++visited > 4000 || clock.ElapsedMilliseconds > 1500)
                        return (Rank(candidates, root), true);
                    if (!CatalogPaths.SafeLeaf(Path.GetFileName(path))) continue;
                    var attrs = File.GetAttributes(path);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        if (folder.Depth < 5) pending.Enqueue((path, folder.Depth + 1));
                        else limited = true;
                    }
                    else if (CatalogPaths.IsExecutable(path) && CatalogPaths.DefaultRole(path) == CatalogMatchKind.Unknown &&
                        !IsSupportFile(path, root))
                        candidates.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { limited = true; }
        }
        return (Rank(candidates, root), limited);
    }

    private static bool IsSupportFile(string path, string root)
    {
        string name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        return name is "7Z" or "7ZR" or "UNRAR" or "DXSETUP" or "PREREQUISITES" or "WERFAULT" ||
            name.Contains("CRASH") || name.Contains("REDIST") || name.Contains("DIAGNOSTIC") ||
            name.Contains("UPDATER") || name.Contains("UNINSTALL") || name.Contains("LAUNCHHELPER") ||
            Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).SkipLast(1).Any(part =>
                part.ToUpperInvariant() is "_COMMONREDIST" or "REDIST" or "PREREQUISITES" or "INSTALLERS" or "SUPPORT");
    }

    private static string[] Rank(List<string> paths, string root)
    {
        string folder = Path.GetFileName(root);
        return paths.OrderByDescending(path => Path.GetFileNameWithoutExtension(path)
                .StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => Path.GetRelativePath(root, path).Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string FriendlyName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return FriendlyName(path, info.ProductName, info.FileDescription);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return FriendlyName(path, null, null);
    }

    internal static string FriendlyName(string path, string? productName, string? fileDescription)
    {
        foreach (var value in new[] { productName?.Trim(), fileDescription?.Trim() })
            if (value is { Length: > 0 and <= 120 } && !GenericProductNames.Contains(value))
                return value;
        return Path.GetFileNameWithoutExtension(path).Replace('_', ' ');
    }

    private static string CheckLocal(string input, bool directory)
    {
        string? path = CatalogPaths.LocalPath(input);
        if (path == null) throw new IOException("Choose a path on a local drive.");
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("Choose a file or folder on a local fixed or removable drive.");
        var parts = new Stack<string>();
        for (string? part = path; part != null; part = Path.GetDirectoryName(part)) parts.Push(part);
        while (parts.TryPop(out var part))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(part); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("The selected file, folder, or shortcut target is missing or inaccessible.", ex);
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Choose the actual local file or folder instead of a linked location.");
            if (((attributes & FileAttributes.Directory) != 0) != (parts.Count > 0 || directory))
                throw new IOException(directory ? "Choose an installation folder." : "Choose an executable file or shortcut.");
        }
        return path;
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int length, IntPtr findData, uint flags);
    }
}
