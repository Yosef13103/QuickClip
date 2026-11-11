using System.Runtime.CompilerServices;

namespace QuickClip.Tests;

internal static class TestSetup
{
    /// <summary>Keep logs and all profile paths inside a fresh test directory.</summary>
    [ModuleInitializer]
    internal static void IsolateProfile()
    {
        AppProfile.TestRoot = Path.Combine(Path.GetTempPath(), $"quickclip-test-profile-{Guid.NewGuid():N}");
        AppSettings.SaveDisabled = true;
        if (Environment.GetEnvironmentVariable("QUICKCLIP_CI") == "1")
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
    }
}
