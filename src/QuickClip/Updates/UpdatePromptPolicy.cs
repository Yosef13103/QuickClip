namespace QuickClip.Updates;

// Exact version suppression applies only to automatic prompts; manual check
// and verified package selection remain independent.
internal sealed class UpdatePromptPolicy
{
    private readonly HashSet<AppVersion> _dismissed = [];

    internal bool ShouldOffer(ReleaseInfo release, AppVersion current, bool enabled, string? skippedVersion) =>
        enabled && release.Version > current && !release.Version.IsPrerelease &&
        Updater.TryPersonalVersion(release.Tag, out var parsed) && parsed == release.Version &&
        !string.Equals(skippedVersion, release.Version.ToString(), StringComparison.Ordinal) &&
        !_dismissed.Contains(release.Version);

    internal void Dismiss(AppVersion version) => _dismissed.Add(version);

    internal static string Notes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "Release notes are unavailable. You can review the release page in Settings.";
        // Plain text only; bound the amount rendered and omit control characters.
        string text = new(body.Take(8000).Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
        return body.Length > 8000 ? text + "\n\nMore details are available on the release page." : text;
    }
}
