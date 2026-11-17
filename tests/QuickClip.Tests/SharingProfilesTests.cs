using System.Globalization;

namespace QuickClip.Tests;

public sealed class SharingProfilesTests
{
    [Fact]
    public void Legacy_limit_gets_a_stable_default_and_round_trips()
    {
        var settings = AppSettings.Parse("""{"ShareSizeMB":64.5,"ShareMotionBlur":false}""");
        var profile = SharingProfiles.Default(settings);
        Assert.Equal(64.5, profile.LimitMB);
        Assert.Equal(profile, SharingProfiles.Default(AppSettings.Parse(settings.ToJson())));
        Assert.False(settings.ShareMotionBlur);
    }

    [Fact]
    public void Corrupt_profiles_and_deleted_default_preserve_valid_settings()
    {
        var first = new SharingProfile(Guid.NewGuid(), "Email", 25);
        var second = new SharingProfile(Guid.NewGuid(), "Discord Nitro", 100);
        var settings = new AppSettings
        {
            ShareProfiles = [second, first, new(Guid.NewGuid(), "email", 30), new(Guid.Empty, "Broken", 0)],
            DefaultShareProfile = Guid.NewGuid().ToString(),
            ShareMotionBlur = false,
        };
        var reloaded = AppSettings.Parse(settings.ToJson().Replace("\"LimitMB\": 0", "\"LimitMB\": \"wrong\""));
        Assert.False(reloaded.ShareMotionBlur);
        Assert.Equal(new[] { second, first }, SharingProfiles.Get(reloaded));
        Assert.Equal(second, SharingProfiles.Default(reloaded));
        reloaded.ShareProfiles = [first];
        Assert.Equal(first, SharingProfiles.Default(reloaded));
    }

    [Fact]
    public void Positive_finite_size_uses_current_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(SharingProfiles.TryLimit("12,5", out double size));
            Assert.Equal(12.5, size);
            foreach (string invalid in new[] { "NaN", "Infinity", "0", "-3", "4001", "12.5", "0,0000001" })
                Assert.False(SharingProfiles.TryLimit(invalid, out _));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
