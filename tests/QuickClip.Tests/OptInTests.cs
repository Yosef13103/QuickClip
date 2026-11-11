namespace QuickClip.Tests;

// These tests use the real desktop, audio endpoints or Windows Recycle Bin.
internal sealed class OptInFactAttribute : FactAttribute
{
    public OptInFactAttribute(string environmentVariable)
    {
        if (Environment.GetEnvironmentVariable("QUICKCLIP_CI") == "1")
            Skip = "Headless CI excludes Windows side-effect and hardware tests.";
        else if (Environment.GetEnvironmentVariable(environmentVariable) != "1")
            Skip = $"Set {environmentVariable}=1 to allow this test's Windows side effects.";
    }
}

internal sealed class CaptureTheoryAttribute : TheoryAttribute
{
    public CaptureTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("QUICKCLIP_CI") == "1")
            Skip = "Headless CI does not record desktop or audio.";
        else if (Environment.GetEnvironmentVariable("QUICKCLIP_TEST_CAPTURE") != "1")
            Skip = "Set QUICKCLIP_TEST_CAPTURE=1 to permit live desktop and audio capture.";
    }
}
