using QuickClip.Updates;

namespace QuickClip.Tests;

public sealed class UpdatePromptPolicyTests
{
    private static readonly AppVersion Current = new(1, 2, 2, null);
    private static ReleaseInfo Release(int patch) =>
        new(new(1, 2, patch, null), $"personal-v1.2.{patch}", "QuickClip", "", null, null);

    [Fact]
    public void Dismissal_is_session_local_and_skip_is_exact_version()
    {
        var policy = new UpdatePromptPolicy();
        var update = Release(3);
        Assert.True(policy.ShouldOffer(update, Current, true, null));
        Assert.False(policy.ShouldOffer(update, Current, false, null));
        policy.Dismiss(update.Version);
        Assert.False(policy.ShouldOffer(update, Current, true, null));
        var restarted = new UpdatePromptPolicy();
        Assert.True(restarted.ShouldOffer(update, Current, true, null));
        Assert.False(restarted.ShouldOffer(update, Current, true, "1.2.3"));
        Assert.True(restarted.ShouldOffer(Release(4), Current, true, "1.2.3"));
        Assert.False(restarted.ShouldOffer(Release(2), Current, true, null));
        Assert.False(restarted.ShouldOffer(update with { Tag = "v1.2.3" }, Current, true, null));
    }

    [Fact]
    public void Skip_preference_survives_settings_roundtrip_and_notes_are_bounded_plain_text()
    {
        var settings = AppSettings.Parse(new AppSettings { SkippedUpdateVersion = "1.2.3", CheckForUpdates = false }.ToJson());
        Assert.Equal("1.2.3", settings.SkippedUpdateVersion);
        Assert.False(settings.CheckForUpdates);
        Assert.Contains("unavailable", UpdatePromptPolicy.Notes(null));
        Assert.Equal("<script>text</script>\nnew", UpdatePromptPolicy.Notes("<script>text</script>\0\nnew"));
        Assert.InRange(UpdatePromptPolicy.Notes(new string('x', 10000)).Length, 8000, 8100);
    }
}
