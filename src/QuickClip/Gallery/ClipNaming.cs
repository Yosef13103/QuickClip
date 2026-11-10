using System.Globalization;
using System.Text.RegularExpressions;

namespace QuickClip.Gallery;

internal static partial class ClipNaming
{
    public const string DefaultTemplate = "{game}__{yyyy-MM-dd}__{HH-mm-ss}_{custom_title}";
    private const int MaxPath = 240;

    public static string Stem(string? template, string game, DateTimeOffset capture, string? title)
    {
        template = (template ?? DefaultTemplate).Trim();
        if (template.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) template = template[..^4];
        if (string.IsNullOrWhiteSpace(template) || Tokens().Replace(template, "").IndexOfAny(['{', '}']) >= 0)
            throw new ArgumentException("Use {game}, {yyyy-MM-dd}, {HH-mm-ss}, and {custom_title} as tokens.");

        if (string.IsNullOrWhiteSpace(title))
            template = EmptyTitle().Replace(template, m =>
            {
                if (m.Index == 0 || m.Index + m.Length == template.Length) return "";
                return m.Groups[2].Length > 0 ? m.Groups[2].Value : m.Groups[1].Value;
            });

        return SafeComponent(Tokens().Replace(template, m => m.Value switch
        {
            "{game}" => game,
            "{yyyy-MM-dd}" => capture.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "{HH-mm-ss}" => capture.ToString("HH-mm-ss", CultureInfo.InvariantCulture),
            _ => title ?? "",
        }));
    }

    public static string SafeComponent(string value, int limit = 200)
    {
        const string invalid = "<>:\"/\\|?*";
        var cleaned = new string(value.Select(c => char.IsControl(c) || invalid.Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(c => c is '.' or '_' or ' ')) cleaned = "Clip";
        if (Reserved().IsMatch(cleaned)) cleaned = "_" + cleaned;
        if (cleaned.Length <= limit) return cleaned;
        int end = char.IsHighSurrogate(cleaned[limit - 1]) ? limit - 1 : limit;
        string shortened = cleaned[..end].TrimEnd('.', ' ');
        return Reserved().IsMatch(shortened) ? "_" + shortened : shortened;
    }

    public static string Candidate(string directory, string stem, string extension, int ordinal = 1)
    {
        string suffix = ordinal == 1 ? "" : $" ({ordinal})";
        int length = Math.Min(200, MaxPath - Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).Length
            - 1 - suffix.Length - extension.Length);
        if (length < 8) throw new IOException("Choose a shorter output directory for this clip.");
        return Path.Combine(directory, SafeComponent(stem, length) + suffix + extension);
    }

    public static string AvailablePath(string directory, string stem, string extension)
    {
        for (int i = 1; ; i++)
        {
            string path = Candidate(directory, stem, extension, i);
            if (!Path.Exists(path)) return path;
        }
    }

    public static string Publish(string staged, string directory, string stem, string extension)
    {
        for (int i = 1; ; i++)
        {
            string destination = Candidate(directory, stem, extension, i);
            if (Path.Exists(destination)) continue;
            try
            {
                File.Move(staged, destination);
                return destination;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 80 or 183) { }
        }
    }

    public static string ReserveStagingPath(string directory, string extension)
    {
        string path = Path.GetFullPath(Path.Combine(directory, $".quickclip-tmp-{Guid.NewGuid():N}{extension}"));
        if (path.Length > MaxPath) throw new IOException("Choose a shorter output directory for this clip.");
        using (new FileStream(path, FileMode.CreateNew)) { }
        return path;
    }

    [GeneratedRegex(@"\{(?:game|custom_title|yyyy-MM-dd|HH-mm-ss)\}")]
    private static partial Regex Tokens();

    [GeneratedRegex(@"([_ .-]*)\{custom_title\}([_ .-]*)")]
    private static partial Regex EmptyTitle();

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³]) *(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reserved();
}
