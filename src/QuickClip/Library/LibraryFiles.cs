using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using QuickClip.Discovery;
using QuickClip.Gallery;

namespace QuickClip.Library;

internal static class LibraryFiles
{
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ProjectLibrary.FolderName, "Cache", "thumbs", "$Recycle.Bin", "System Volume Information"
    };
    private const FileAttributes UnavailableAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline;
    private const FileAttributes SkipAttributes = FileAttributes.Hidden | FileAttributes.System;
    public static string PathKey(string path) => path.ToUpperInvariant();
    public static string TextKey(string value) => value.Normalize().ToUpperInvariant();
    public static bool Within(string path, string directory) => CatalogPaths.IsWithin(path, directory);

    public static string RootPath(string path)
    {
        var full = CatalogPaths.LocalPath(path);
        if (full == null || full == Path.GetPathRoot(full) || ClipLibrary.IsMomentsPath(full) ||
            full.Split(Path.DirectorySeparatorChar).Any(ExcludedNames.Contains) ||
            Path.GetFileName(full).Equals("QuickClip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a specific media directory outside archive, project and cache folders.", nameof(path));
        return full;
    }

    public static string RelativePath(string path)
    {
        string normalized = path.Replace('/', '\\').TrimEnd('\\');
        if (normalized.Length == 0 || normalized.Split('\\').Any(part => !CatalogPaths.SafeLeaf(part)))
            throw new ArgumentException("Use a relative directory beneath the approved root.", nameof(path));
        return normalized;
    }

    public static bool IsExcluded(string root, string path, ImmutableArray<string> exclusions)
    {
        var relative = Path.GetRelativePath(root, path);
        return ClipLibrary.IsMomentsPath(path) || relative.Split(Path.DirectorySeparatorChar).Any(part => ExcludedNames.Contains(part)
            || part.Equals("QuickClip", StringComparison.OrdinalIgnoreCase)) ||
            exclusions.Any(directory => Within(relative, directory));
    }

    // Check ancestors from the drive down, before asking the OS to resolve a descendant.
    public static ClipAvailability InspectPath(string path, bool directory)
    {
        var ancestors = new Stack<string>();
        for (string? current = path; current != null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
        try
        {
            while (ancestors.TryPop(out string? current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & UnavailableAttributes) != 0 ||
                    ((attributes & FileAttributes.Directory) != 0) != (directory || ancestors.Count > 0))
                    return ClipAvailability.Unavailable;
            }
            return ClipAvailability.Available;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return ClipAvailability.Missing; }
        catch (Exception ex) when (IsAccessError(ex)) { return ClipAvailability.Unavailable; }
    }

    public static bool IsAccessError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    public static bool EligibleFile(string path)
    {
        if (!ClipLibrary.IsClipFile(path)) return false;
        string stem = Path.GetFileNameWithoutExtension(path);
        return !stem.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) &&
            !stem.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
            !stem.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
            !stem.EndsWith(".temp", StringComparison.OrdinalIgnoreCase);
    }

    public static string? RootVolume(string path)
    {
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        return handle.IsInvalid ? null : NativeIdentity(handle)?.Volume;
    }

    public static SafeFileHandle OpenMedia(string path)
    {
        // Open the final path component itself, even if it changed to a link after enumeration.
        var handle = CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            try
            {
                if (!IsUnavailable(File.GetAttributes(handle))) return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        handle.Dispose();
        throw new IOException("The media file is unreadable or is a reparse point.");
    }

    public static FileStamp ReadStamp(SafeFileHandle handle)
    {
        var id = NativeIdentity(handle);
        return new(File.GetCreationTimeUtc(handle).Ticks, File.GetLastWriteTimeUtc(handle).Ticks,
            RandomAccess.GetLength(handle), id is { } key ? $"{key.Volume}:{key.Low:X16}{key.High:X16}" : null);
    }

    private static (string Volume, ulong Low, ulong High)? NativeIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 18, out var info, 24)) return null;
        return info.Low == 0 && info.High == 0 ? null : ($"{info.Volume:X16}", info.Low, info.High);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileId { public ulong Volume, Low, High; }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out NativeFileId info, uint size);

    public static bool IsUnavailable(FileAttributes attributes) => (attributes & UnavailableAttributes) != 0;
    public static bool ShouldSkip(FileAttributes attributes) => (attributes & SkipAttributes) != 0;
}

internal readonly record struct FileStamp(long Created, long Modified, long Bytes, string? Identity)
{
    public bool SameOccupant(FileStamp other) => Created == other.Created &&
        (Identity != null && Identity == other.Identity ||
         Identity == null && other.Identity == null && Modified == other.Modified && Bytes == other.Bytes);
}

internal sealed record LegacyRead(LegacyMetadataStatus Status, ClipDetails? Details = null);

internal static class LegacyLibraryMetadata
{
    private sealed record Entry(long CreatedTicks, long ModifiedTicks, long Length, ClipDetails? Details);

    public static LegacyRead Read(string? directory, string path, FileStamp stamp)
    {
        if (directory == null) return new(LegacyMetadataStatus.None);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        string file = Path.Combine(directory, key + ".json");
        try
        {
            if (LibraryFiles.InspectPath(file, directory: false) is { } state && state != ClipAvailability.Available)
                return new(state == ClipAvailability.Missing ? LegacyMetadataStatus.None : LegacyMetadataStatus.Unreadable);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 1024 * 1024) return new(LegacyMetadataStatus.Unreadable);
            var entry = JsonSerializer.Deserialize<Entry>(stream);
            if (entry?.Details is not { Game.Length: > 0 } details || details.CapturedAt == default)
                return new(LegacyMetadataStatus.Unreadable);
            if (entry.CreatedTicks != stamp.Created || entry.ModifiedTicks != stamp.Modified || entry.Length != stamp.Bytes)
                return new(LegacyMetadataStatus.StatMismatch);
            return new(LegacyMetadataStatus.Imported, details);
        }
        catch (Exception ex) when (LibraryFiles.IsAccessError(ex) || ex is JsonException or NotSupportedException)
        {
            return new(LegacyMetadataStatus.Unreadable);
        }
    }
}
