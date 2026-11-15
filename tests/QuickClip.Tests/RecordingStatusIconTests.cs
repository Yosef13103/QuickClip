using QuickClip.Recording;
using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class RecordingStatusIconTests
{
    [Theory]
    [InlineData(false, false, RecordingMode.Off, 0)]
    [InlineData(true, false, RecordingMode.Armed, 1)]
    [InlineData(true, false, RecordingMode.Recovering, 1)]
    [InlineData(true, true, RecordingMode.GameTriggered, 2)]
    [InlineData(true, true, RecordingMode.ManualDesktop, 3)]
    [InlineData(false, true, RecordingMode.ManualDesktop, 0)]
    public void Badge_tracks_master_and_actual_capture(bool enabled, bool running, RecordingMode mode, int expected)
    {
        var state = new RecorderState(running, null, "", [], []) { Mode = mode };
        Assert.Equal((RecordingBadge)expected, RecordingStatusIcon.From(enabled, state));
    }
}
