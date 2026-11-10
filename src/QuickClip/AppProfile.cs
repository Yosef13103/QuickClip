using System.Security.Cryptography;
using System.Text;

namespace QuickClip;

/// <summary>Separates the personal build's persisted data and process identity from upstream QuickClip.</summary>
internal static class AppProfile
{
#if QUICKCLIP_PERSONAL
    public static bool IsPersonal => true;
#else
    public static bool IsPersonal => false;
#endif

    public static string Name => IsPersonal ? "QuickClip Personal" : "QuickClip";
    private static string InstanceId => !IsPersonal ? "QuickClip.Instance"
        : PortableRoot.Value is string root ? "QuickClip.Personal.Instance." + ProfileKey(root) : "QuickClip.Personal.Instance";
    public static string MutexName => @"Local\" + InstanceId;
    public static string PipeName => InstanceId + "." + Environment.UserName;

    private static readonly Lazy<string?> PortableRoot = new(ReadPortableRoot);
    internal static string? TestRoot { private get; set; }
    private static string? DataRoot => TestRoot ?? PortableRoot.Value;

    public static string SettingsDirectory => DirectoryFor(Environment.SpecialFolder.ApplicationData, "settings", Name);
    public static string LocalDirectory => DirectoryFor(Environment.SpecialFolder.LocalApplicationData, "local", Name);
    public static string ClipsDirectory => DirectoryFor(Environment.SpecialFolder.MyVideos, "clips", Name);
    public static string SharedDirectory => DirectoryFor(Environment.SpecialFolder.MyVideos, "shared", Name + " Shared");
    public static string TempDirectory => DataRoot is string root ? Path.Combine(root, "temp") : Path.Combine(Path.GetTempPath(), Name);

    private static string DirectoryFor(Environment.SpecialFolder folder, string portableName, string name) =>
        DataRoot is string root ? Path.Combine(root, portableName) : Path.Combine(Environment.GetFolderPath(folder), name);

    private static string? ReadPortableRoot()
    {
        if (!IsPersonal || Environment.GetEnvironmentVariable("QUICKCLIP_PERSONAL_DATA_DIR") is not { Length: > 0 } path)
            return null;
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("QUICKCLIP_PERSONAL_DATA_DIR must be an absolute directory path.");
        return Path.GetFullPath(path);
    }

    private static string ProfileKey(string root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(root).ToUpperInvariant())))[..24];
}
